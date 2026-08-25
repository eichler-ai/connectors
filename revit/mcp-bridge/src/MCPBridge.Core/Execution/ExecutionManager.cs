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
        CancellationTokenSource? ctsToDispose = null;
        DiagnosticRecord? diagnostic;

        lock (_lock)
        {
            if (!_ringBuffer.TryGet(executionId, out var record) || record is null)
            {
                // Second review, Fix 5: an unknown/missing execution_id (e.g. the ring buffer evicted the
                // one execution that's actually in flight -- see ExecutionRingBuffer.Prune's non-terminal
                // exemption, which should make this unreachable in practice, but defense in depth matters
                // here specifically because this can be invoked from Revit's UI-thread Execute() callback)
                // must never throw -- treat it the same as the terminal race below: a diagnostic, not an
                // exception.
                return UnknownExecutionDiagnostic(executionId, transitionName);
            }

            if (record.Status.IsTerminal())
            {
                return RaceDiagnostic(record, transitionName);
            }

            mutate(record);
            if (clearActive)
            {
                ClearActiveIfMatches(executionId, out ctsToDispose);
            }

            diagnostic = null;
        }

        // Fix 6: Dispose() outside the lock -- it can block waiting for an in-flight CTS callback, which
        // must never happen while other threads (including Revit's UI thread) are blocked trying to
        // acquire _lock for their own Complete*/MarkRunning calls.
        ctsToDispose?.Dispose();
        return diagnostic;
    }

    public CancellationRequestOutcome RequestCancellation(Guid executionId, DateTimeOffset now)
    {
        CancellationTokenSource? ctsToCancel = null;
        CancellationTokenSource? ctsToDispose = null;
        CancellationRequestOutcome outcome;

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

            ApplyCancellation(record, now, out ctsToCancel, out ctsToDispose);
            outcome = CancellationRequestOutcome.Acknowledged;
        }

        // Fix 6: Cancel()/Dispose() outside the lock -- Cancel() runs registered callbacks synchronously on
        // this thread, and a script may have registered one on its ScriptGlobals.CancellationToken; doing
        // this under _lock risks blocking it (and therefore Revit's UI thread) for an unbounded time.
        ctsToCancel?.Cancel();
        ctsToDispose?.Dispose();
        return outcome;
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
        CancellationTokenSource? ctsToCancel = null;
        CancellationTokenSource? ctsToDispose = null;

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
            if (elapsedMs < record.MaxDurationMs)
            {
                return;
            }

            ApplyCancellation(record, now, out ctsToCancel, out ctsToDispose);
        }

        // Fix 6: outside the lock, same reasoning as RequestCancellation.
        ctsToCancel?.Cancel();
        ctsToDispose?.Dispose();
    }

    /// <summary>
    /// Shared cancel logic for <see cref="RequestCancellation"/> (manual cancel_execution) and
    /// <see cref="CheckMaxDuration"/> (auto-cancel) -- second review, Fixes 3 &amp; 4. Caller must already
    /// hold <see cref="_lock"/>; the caller is responsible for calling Cancel()/Dispose() on the returned
    /// CancellationTokenSource references AFTER releasing the lock (Fix 6).
    ///
    /// A still-Pending record (nothing has started running -- there's nothing to cooperatively/forcibly
    /// stop) resolves directly and immediately to Cancelled via the same record-mutation +
    /// ClearActiveIfMatches path Transition() uses elsewhere in this file, rather than merely stamping
    /// CancellationRequestedAt and falling through to CheckGraceExpiry's generic "didn't stop in time ->
    /// Unrecoverable" escalation -- that escalation exists for a script actually in flight, which a Pending
    /// execution by definition is not (Fix 4). A Running record goes through that flow as before: stamp
    /// CancellationRequestedAt and hand back its CancellationTokenSource for the caller to Cancel() (Fix 3
    /// -- previously only the record was stamped and the token itself was never actually cancelled from
    /// this path, so a cooperative script polling it never observed a max-duration timeout).
    /// </summary>
    private void ApplyCancellation(ExecutionRecord record, DateTimeOffset now, out CancellationTokenSource? ctsToCancel, out CancellationTokenSource? ctsToDispose)
    {
        ctsToCancel = null;
        ctsToDispose = null;

        if (record.Status == ExecutionStatus.Pending)
        {
            record.MarkCancelled(now, stdOut: null);
            ClearActiveIfMatches(record.ExecutionId, out ctsToDispose);
            return;
        }

        record.RequestCancellation(now);
        _cancellationSources.TryGetValue(record.ExecutionId, out ctsToCancel);
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

            // Fix 6 nuance: deliberately do NOT Dispose() here (unlike every other path in this class). At
            // grace expiry the script may still be running -- that's the definition of grace expiry -- and
            // could still touch its CancellationToken (e.g. token.WaitHandle, or a linked CTS) via a
            // still-live reference; disposing the source out from under it risks ObjectDisposedException
            // inside the script. Cancel() was already called (idempotently safe) whenever cancellation was
            // first requested/escalated -- just stop tracking the source here and let it be GC'd once the
            // script (and its last reference to the token) actually lets go of it.
            _cancellationSources.Remove(record.ExecutionId);
        }
    }

    public ExecutionRecord? Poll(Guid executionId)
    {
        lock (_lock)
        {
            _ringBuffer.TryGet(executionId, out var record);
            return record;
        }
    }

    /// <summary>Caller must already hold <see cref="_lock"/>. Returns the removed CancellationTokenSource (if any) via <paramref name="removedCts"/> so the caller can Cancel()/Dispose() it AFTER releasing the lock (Fix 6) -- this method itself never calls either.</summary>
    private void ClearActiveIfMatches(Guid executionId, out CancellationTokenSource? removedCts)
    {
        if (_active?.ExecutionId == executionId)
        {
            _active = null;
        }

        _cancellationSources.Remove(executionId, out removedCts);
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

    /// <summary>
    /// Built when a finishing-path transition (MarkRunning/CompleteSuccess/CompleteError/
    /// CompleteCancelled) can't find the record at all -- most likely it was evicted from the ring
    /// buffer (ExecutionRingBuffer.Prune() is expected to exempt non-terminal records from eviction, so
    /// this should be unreachable in practice, but this is the defense-in-depth path for if that ever
    /// isn't true). Same treatment as <see cref="RaceDiagnostic"/>: a diagnostic, never a thrown exception,
    /// since this can be invoked from Revit's UI thread.
    /// </summary>
    private static DiagnosticRecord UnknownExecutionDiagnostic(Guid executionId, string attemptedTransition) => DiagnosticRecord.Create(
        DiagnosticSeverity.Warning,
        "execution-transition-unknown-execution-id",
        DiagnosticSource.Execution,
        $"execution {executionId} was not found (most likely evicted from the ring buffer) by the time " +
        $"'{attemptedTransition}' arrived; the late transition was ignored.",
        detail: new Dictionary<string, object?>
        {
            ["execution_id"] = executionId.ToString(),
            ["attempted_transition"] = attemptedTransition,
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
