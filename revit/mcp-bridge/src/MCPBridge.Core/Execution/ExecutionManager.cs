using System;
using System.Collections.Generic;
using System.Threading;
using MCPBridge.Core.Diagnostics;

namespace MCPBridge.Core.Execution;

/// <summary>
/// The pending/running/busy/cancelled/unrecoverable state machine (PRD §06). One
/// instance is scoped to a single Revit instance: since Revit's UI thread runs one
/// script at a time, there is at most one active (non-terminal) execution at any
/// point, and a second execute_script while one is active returns Busy pointing at
/// the one already running -- never queues silently.
///
/// All time-dependent behavior (max_duration_ms auto-cancel, the cancellation grace
/// period) takes `now` as an explicit parameter rather than reading the wall clock
/// or owning a Timer, so it's fully deterministic to unit test; a caller (the AddIn
/// wiring) is expected to drive CheckMaxDuration/CheckGraceExpiry periodically.
/// </summary>
public sealed class ExecutionManager
{
    private readonly ExecutionRingBuffer _ringBuffer;
    private readonly TimeSpan _gracePeriod;
    private readonly object _lock = new();

    private ExecutionRecord? _active;
    private bool _instanceUnrecoverable;

    // Repo note (pre-existing, phase-01): no CancellationTokenSource existed anywhere in src/ --
    // cancellation was plumbed through types (ScriptGlobals.CancellationToken) but never wired up, so
    // cancel_execution could not actually stop even a cooperative script. Wired here: one CTS per
    // execution, created in Start() and cancelled in RequestCancellation(), keyed by execution_id so a
    // caller can fetch the right Token for the ScriptGlobals it builds via GetCancellationToken(). Kept
    // under the same _lock as everything else in this class for the same reason as the Fix 4 mutations
    // above -- Cancel() and disposal must never race a concurrent terminal-state transition.
    private readonly Dictionary<Guid, CancellationTokenSource> _cancellationSources = new();

    public ExecutionManager(ExecutionRingBuffer ringBuffer, TimeSpan gracePeriod)
    {
        _ringBuffer = ringBuffer;
        _gracePeriod = gracePeriod;
    }

    /// <summary>Default grace period per PRD §06: ~5-10s.</summary>
    public static ExecutionManager CreateDefault(ExecutionRingBuffer ringBuffer) =>
        new(ringBuffer, TimeSpan.FromSeconds(7));

    public bool IsInstanceUnrecoverable
    {
        get { lock (_lock) { return _instanceUnrecoverable; } }
    }

    public ExecuteOutcome Start(string scriptText, long maxDurationMs, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_instanceUnrecoverable)
            {
                return ExecuteOutcome.InstanceUnrecoverable(UnrecoverableDiagnostic());
            }

            if (_active is { Status: var status } && !status.IsTerminal())
            {
                return ExecuteOutcome.Busy(_active);
            }

            var record = ExecutionRecord.CreatePending(Guid.NewGuid(), scriptText, maxDurationMs, now);
            _active = record;
            _ringBuffer.Add(record);
            _cancellationSources[record.ExecutionId] = new CancellationTokenSource();
            return ExecuteOutcome.Started(record);
        }
    }

    /// <summary>
    /// The CancellationToken a caller should build this execution's ScriptGlobals with. Returns
    /// CancellationToken.None for an unknown execution_id rather than throwing, so a caller that raced a
    /// ring-buffer eviction fails softly (the script just won't observe cancellation) instead of taking
    /// down whatever's building the globals.
    /// </summary>
    public CancellationToken GetCancellationToken(Guid executionId)
    {
        lock (_lock)
        {
            return _cancellationSources.TryGetValue(executionId, out var cts) ? cts.Token : CancellationToken.None;
        }
    }

    /// <summary>Pending -&gt; Running. See <see cref="Transition"/> for why this never throws on a terminal race.</summary>
    public DiagnosticRecord? MarkRunning(Guid executionId, DateTimeOffset now) =>
        Transition(executionId, "mark-running", record => record.MarkRunning(now), clearActive: false);

    /// <summary>See <see cref="Transition"/> for why this never throws on a terminal race.</summary>
    public DiagnosticRecord? CompleteSuccess(Guid executionId, DateTimeOffset now, object? result, string? stdOut, IReadOnlyList<DiagnosticRecord> notices) =>
        Transition(executionId, "complete-success", record => record.MarkCompleted(now, result, stdOut, notices), clearActive: true);

    /// <summary>See <see cref="Transition"/> for why this never throws on a terminal race.</summary>
    public DiagnosticRecord? CompleteError(Guid executionId, DateTimeOffset now, DiagnosticRecord error, string? stdOut) =>
        Transition(executionId, "complete-error", record => record.MarkError(now, error, stdOut), clearActive: true);

    /// <summary>See <see cref="Transition"/> for why this never throws on a terminal race.</summary>
    public DiagnosticRecord? CompleteCancelled(Guid executionId, DateTimeOffset now, string? stdOut) =>
        Transition(executionId, "complete-cancelled", record => record.MarkCancelled(now, stdOut), clearActive: true);

    /// <summary>
    /// Shared guard for every finishing-path record mutation (MarkRunning/CompleteSuccess/
    /// CompleteError/CompleteCancelled). Runs under <see cref="_lock"/> -- the same lock
    /// CheckGraceExpiry/CheckMaxDuration use -- specifically so a mutation can never race the
    /// grace-timer flipping the record to a terminal state out from under it (PR #2 review
    /// finding: a prior version mutated records outside this lock, so a script finishing right
    /// as the grace timer fired could throw InvalidOperationException from inside Execute() on
    /// Revit's UI thread -- a crash-class bug, not just a logic bug). If the record already
    /// went terminal (e.g. the grace period expired first), this is a benign no-op that returns
    /// a diagnostic instead of throwing, so a caller on the UI thread can log/report it and
    /// safely return -- centralized here (rather than duplicated per call site) so a future
    /// transition method gets this safety by construction, not by convention.
    /// </summary>
    private DiagnosticRecord? Transition(Guid executionId, string transitionName, Action<ExecutionRecord> mutate, bool clearActive)
    {
        lock (_lock)
        {
            var record = RequireRecord(executionId);
            if (record.Status.IsTerminal())
            {
                return RaceDiagnostic(record, transitionName);
            }

            mutate(record);
            if (clearActive)
            {
                ClearActiveIfMatches(executionId);
            }

            return null;
        }
    }

    public CancellationRequestOutcome RequestCancellation(Guid executionId, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_ringBuffer.TryGet(executionId, out var record) || record is null)
            {
                return CancellationRequestOutcome.NotFound;
            }

            if (record.Status.IsTerminal())
            {
                return CancellationRequestOutcome.AlreadyTerminal;
            }

            record.RequestCancellation(now);
            if (_cancellationSources.TryGetValue(executionId, out var cts))
            {
                cts.Cancel();
            }

            return CancellationRequestOutcome.Acknowledged;
        }
    }

    /// <summary>
    /// Auto-cancels the active execution once max_duration_ms has elapsed (PRD §06).
    /// Idempotent. Applies to both Pending and Running executions -- not just Running --
    /// so a script stuck behind a modal Revit dialog before it ever reaches Execute()
    /// still gets cancelled instead of sitting Pending forever (PR #2 review finding;
    /// this is the PRD §06 headline case for max_duration_ms). Measured from CreatedAt
    /// (when the execution was queued), not StartedAt: StartedAt is only stamped on the
    /// Pending -&gt; Running transition, which a Pending execution stuck behind a dialog may
    /// never reach, so a start-of-Running reference time would never fire for exactly the
    /// case this exists to catch. max_duration_ms is the agent's budget for the whole
    /// execute_script call, queue wait included, not just active running time.
    /// </summary>
    public void CheckMaxDuration(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_active is not { } record || record.Status is not (ExecutionStatus.Pending or ExecutionStatus.Running))
            {
                return;
            }

            if (record.CancellationRequestedAt is not null)
            {
                return;
            }

            var elapsedMs = (now - record.CreatedAt).TotalMilliseconds;
            if (elapsedMs >= record.MaxDurationMs)
            {
                record.RequestCancellation(now);
            }
        }
    }

    /// <summary>
    /// If cancellation was requested and the grace period has lapsed without the
    /// execution reaching a terminal state, flips it (and the whole instance) to
    /// Unrecoverable (PRD §06). No-op if the script already resolved on its own.
    /// </summary>
    public void CheckGraceExpiry(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_active is not { } record || record.Status.IsTerminal())
            {
                return;
            }

            if (record.CancellationRequestedAt is not { } requestedAt)
            {
                return;
            }

            if (now - requestedAt < _gracePeriod)
            {
                return;
            }

            var diagnostic = DiagnosticRecord.Create(
                DiagnosticSeverity.Error,
                "execution-cancellation-grace-expired",
                DiagnosticSource.Execution,
                $"execution {record.ExecutionId} did not stop within the cancellation grace period; " +
                "the instance is now unrecoverable.",
                detail: new Dictionary<string, object?> { ["execution_id"] = record.ExecutionId.ToString() },
                remedy: new[] { "Restart Revit to recover this instance; a fresh instance_id will be issued on reconnect." });

            record.MarkUnrecoverable(now, diagnostic);
            _instanceUnrecoverable = true;
            RemoveCancellationSource(record.ExecutionId);
        }
    }

    public ExecutionRecord? Poll(Guid executionId)
    {
        _ringBuffer.TryGet(executionId, out var record);
        return record;
    }

    private ExecutionRecord RequireRecord(Guid executionId)
    {
        if (!_ringBuffer.TryGet(executionId, out var record) || record is null)
        {
            throw new InvalidOperationException($"Unknown execution_id {executionId}.");
        }

        return record;
    }

    /// <summary>Caller must already hold <see cref="_lock"/>.</summary>
    private void ClearActiveIfMatches(Guid executionId)
    {
        if (_active?.ExecutionId == executionId)
        {
            _active = null;
        }

        RemoveCancellationSource(executionId);
    }

    /// <summary>Caller must already hold <see cref="_lock"/>.</summary>
    private void RemoveCancellationSource(Guid executionId)
    {
        if (_cancellationSources.Remove(executionId, out var cts))
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// Built when a finishing-path transition (MarkRunning/CompleteSuccess/CompleteError/
    /// CompleteCancelled) finds the record already terminal -- i.e. the grace-timer path
    /// won the race and flipped it first. This is expected and benign (PRD §01
    /// observability-over-silence: report it, don't hide it, but also don't throw an
    /// exception a caller on Revit's UI thread can't safely handle).
    /// </summary>
    private static DiagnosticRecord RaceDiagnostic(ExecutionRecord record, string attemptedTransition) => DiagnosticRecord.Create(
        DiagnosticSeverity.Warning,
        "execution-transition-raced-terminal",
        DiagnosticSource.Execution,
        $"execution {record.ExecutionId} was already terminal ({record.Status}) by the time '{attemptedTransition}' " +
        "arrived, most likely because the cancellation grace period expired first; the late transition was ignored.",
        detail: new Dictionary<string, object?>
        {
            ["execution_id"] = record.ExecutionId.ToString(),
            ["attempted_transition"] = attemptedTransition,
            ["actual_status"] = record.Status.ToString(),
        },
        remedy: null);

    private static DiagnosticRecord UnrecoverableDiagnostic() => DiagnosticRecord.Create(
        DiagnosticSeverity.Error,
        "instance-unrecoverable",
        DiagnosticSource.Execution,
        "this Revit instance is unrecoverable after a cancellation that did not complete within its grace period.",
        detail: null,
        remedy: new[] { "Restart Revit to recover this instance; a fresh instance_id will be issued on reconnect." });
}
