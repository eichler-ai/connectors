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
    // execution, created in Start() and cancelled via ApplyCancellation(), keyed by execution_id so a
    // caller can fetch the right Token for the ScriptGlobals it builds via GetCancellationToken().
    //
    // Third review finding: this class deliberately never calls CancellationTokenSource.Dispose(). An
    // earlier version did, with Cancel()/Dispose() both moved outside _lock (so neither could block a
    // UI-thread caller waiting on the lock) -- but that let two independent code paths race for the same
    // CTS: one thread reads it via TryGetValue to Cancel() it outside the lock while another thread's
    // Transition() concurrently removes-and-disposes the same instance, so the first thread's Cancel()
    // call could land on an already-disposed source and throw ObjectDisposedException straight out of
    // RequestCancellation/CheckMaxDuration. None of these CancellationTokenSources ever use CancelAfter or
    // have their .WaitHandle accessed, so they hold no unmanaged resource that actually needs releasing --
    // skipping Dispose() entirely removes the race without reintroducing the original lock-blocking
    // problem, at the cost of a CTS object living until GC reclaims it rather than being deterministically
    // released the moment its execution finishes. Cancel() itself remains idempotent and safe from
    // ObjectDisposedException regardless of ordering, including after removal from this dictionary -- but
    // NOT unconditionally safe: Cancel() still runs any callback a script registered on its
    // ScriptGlobals.CancellationToken synchronously, and a throwing callback surfaces as an
    // AggregateException from Cancel() itself (see SafeCancel, fourth review finding), which is a
    // completely separate hazard from disposal and isn't fixed by anything in this note.
    private readonly Dictionary<string, CancellationTokenSource> _cancellationSources = new();

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

    /// <summary>
    /// Starts a new execution keyed by <paramref name="executionId"/>. Per PRD §01, execution_id
    /// is broker-minted ("the add-in echoes the same ID back rather than generating its own") --
    /// the Go broker mints IDs shaped "exec-&lt;uuid&gt;" and sends one in every execute_script
    /// call's params, so the caller (the TCP-handling code, once wired) passes that string straight
    /// through here rather than this class minting its own Guid.
    ///
    /// Third review finding: unlike a locally-minted Guid, executionId now arrives from the wire --
    /// untrusted input, not a value this class controls the shape or uniqueness of. Every other
    /// public method here is deliberately built never to throw (RequireRecord/Transition return a
    /// diagnostic instead, since they can run on Revit's UI thread where an uncaught exception is a
    /// crash-class bug), but Start is the one true entry point where a caller-supplied executionId is
    /// first accepted -- validating it here, loudly, is what keeps every downstream method able to
    /// keep assuming a well-formed, unique id. A null/empty/whitespace id or one that collides with
    /// an existing ring-buffer entry (which should never happen given the broker's uuid-derived ids,
    /// but would otherwise corrupt ExecutionRingBuffer's _byId mapping -- see its own Add() doc
    /// comment) is rejected with an ArgumentException rather than silently accepted.
    /// </summary>
    public ExecuteOutcome Start(string executionId, string scriptText, long maxDurationMs, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(executionId))
        {
            throw new ArgumentException("executionId must not be null or empty -- it is broker-minted and must be echoed back exactly, per PRD §01.", nameof(executionId));
        }

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

            var record = ExecutionRecord.CreatePending(executionId, scriptText, maxDurationMs, now);
            if (!_ringBuffer.Add(record))
            {
                throw new ArgumentException($"executionId '{executionId}' is already in use by another (possibly still-active) execution.", nameof(executionId));
            }

            _active = record;
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
    public CancellationToken GetCancellationToken(string executionId)
    {
        lock (_lock)
        {
            return _cancellationSources.TryGetValue(executionId, out var cts) ? cts.Token : CancellationToken.None;
        }
    }

    /// <summary>Pending -&gt; Running. See <see cref="Transition"/> for why this never throws on a terminal race.</summary>
    public DiagnosticRecord? MarkRunning(string executionId, DateTimeOffset now) =>
        Transition(executionId, "mark-running", record => record.MarkRunning(now), clearActive: false);

    /// <summary>See <see cref="Transition"/> for why this never throws on a terminal race.</summary>
    public DiagnosticRecord? CompleteSuccess(string executionId, DateTimeOffset now, object? result, string? stdOut, IReadOnlyList<DiagnosticRecord> notices) =>
        Transition(executionId, "complete-success", record => record.MarkCompleted(now, result, stdOut, notices), clearActive: true);

    /// <summary>See <see cref="Transition"/> for why this never throws on a terminal race.</summary>
    public DiagnosticRecord? CompleteError(string executionId, DateTimeOffset now, DiagnosticRecord error, string? stdOut) =>
        Transition(executionId, "complete-error", record => record.MarkError(now, error, stdOut), clearActive: true);

    /// <summary>See <see cref="Transition"/> for why this never throws on a terminal race.</summary>
    public DiagnosticRecord? CompleteCancelled(string executionId, DateTimeOffset now, string? stdOut) =>
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
    private DiagnosticRecord? Transition(string executionId, string transitionName, Action<ExecutionRecord> mutate, bool clearActive)
    {
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
                ClearActiveIfMatches(executionId);
            }

            return null;
        }
    }

    public CancellationRequestOutcome RequestCancellation(string executionId, DateTimeOffset now)
    {
        CancellationTokenSource? ctsToCancel;
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

            ctsToCancel = ApplyCancellation(record, now);
            outcome = CancellationRequestOutcome.Acknowledged;
        }

        // Cancel() outside the lock -- it runs registered callbacks synchronously on this thread, and a
        // script may have registered one on its ScriptGlobals.CancellationToken; doing this under _lock
        // risks blocking it (and therefore Revit's UI thread) for an unbounded time. Safe to call on a CTS
        // this class has since removed from _cancellationSources (e.g. via a concurrent grace expiry) --
        // see the class-level note on why this class never Dispose()s a CancellationTokenSource.
        SafeCancel(ctsToCancel);
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
        CancellationTokenSource? ctsToCancel;

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

            ctsToCancel = ApplyCancellation(record, now);
        }

        // Outside the lock, same reasoning as RequestCancellation.
        SafeCancel(ctsToCancel);
    }

    /// <summary>
    /// Fourth review finding: CancellationTokenSource.Cancel() runs every registered callback
    /// synchronously on the calling thread, and if any callback throws, Cancel() collects the
    /// failure(s) and rethrows as an AggregateException once every callback has run -- this is
    /// documented, unavoidable behavior, distinct from (and not fixed by) this class no longer
    /// Dispose()ing its CancellationTokenSources. The token handed to a running script
    /// (ScriptGlobals.CancellationToken) is exposed by name into arbitrary Roslyn-compiled script
    /// scope, so a script registering a callback that throws -- deliberately, or incidentally
    /// because Revit is in a state the callback didn't expect -- must never be able to propagate
    /// that exception out of RequestCancellation/CheckMaxDuration, both of which can be driven from
    /// Revit's UI thread (CheckMaxDuration per this class's own periodic-pump contract). By the time
    /// any callback runs, Cancel() has already flipped the token's cancelled state, so the
    /// cancellation itself has already taken effect regardless of what a callback does afterward --
    /// swallowing a callback failure here doesn't mask a failed cancellation, only a failed
    /// notification of one.
    /// </summary>
    private static void SafeCancel(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (AggregateException)
        {
        }
    }

    /// <summary>
    /// Shared cancel logic for <see cref="RequestCancellation"/> (manual cancel_execution) and
    /// <see cref="CheckMaxDuration"/> (auto-cancel) -- second review, Fixes 3 &amp; 4. Caller must already
    /// hold <see cref="_lock"/>; the caller is responsible for calling Cancel() on the returned
    /// CancellationTokenSource AFTER releasing the lock (Fix 6).
    ///
    /// A still-Pending record (nothing has started running -- there's nothing to cooperatively/forcibly
    /// stop) resolves directly and immediately to Cancelled via the same record mutation (record.MarkCancelled)
    /// Transition() performs elsewhere in this file -- called directly here, under the caller's already-held
    /// lock, rather than through Transition() itself (this method doesn't get Transition()'s own
    /// ring-buffer/terminal-race guards, because both of this method's callers already did that exact
    /// guarding, under the same lock, immediately before calling in) -- rather than merely stamping
    /// CancellationRequestedAt and falling through
    /// to CheckGraceExpiry's generic "didn't stop in time -> Unrecoverable" escalation -- that escalation
    /// exists for a script actually in flight, which a Pending execution by definition is not (Fix 4).
    ///
    /// Third review finding: a Pending record's CancellationTokenSource must still be Cancel()'d, and its
    /// dictionary entry must NOT be removed. The work item this execution's ExternalEventBridge raise
    /// already queued is still going to fire eventually (nothing can currently un-queue it from Revit's
    /// side -- see the BridgeHost.cs TODO), and per that same TODO the work item's first move must be to
    /// check GetCancellationToken(executionId).IsCancellationRequested and bail out without touching the
    /// model if it's set. Both halves of that contract depend on this dictionary entry surviving with its
    /// token actually cancelled: removing it here would make GetCancellationToken silently return
    /// CancellationToken.None (permanently unset) for an id that legitimately still has a pending raise in
    /// flight, defeating the exact check the TODO mandates and letting an already-cancelled script run
    /// anyway once the dialog/blockage clears. _active is still cleared immediately (via the same
    /// _active-only half of what ClearActiveIfMatches does) so a new execute_script isn't blocked Busy on
    /// an execution that's already terminal from the caller's point of view.
    ///
    /// A Running record goes through the flow as before: stamp CancellationRequestedAt and hand back its
    /// CancellationTokenSource for the caller to Cancel() (Fix 3 -- previously only the record was stamped
    /// and the token itself was never actually cancelled from this path, so a cooperative script polling it
    /// never observed a max-duration timeout).
    /// </summary>
    private CancellationTokenSource? ApplyCancellation(ExecutionRecord record, DateTimeOffset now)
    {
        if (record.Status == ExecutionStatus.Pending)
        {
            record.MarkCancelled(now, stdOut: null);
            if (_active?.ExecutionId == record.ExecutionId)
            {
                _active = null;
            }

            _cancellationSources.TryGetValue(record.ExecutionId, out var pendingCts);
            return pendingCts;
        }

        record.RequestCancellation(now);
        _cancellationSources.TryGetValue(record.ExecutionId, out var runningCts);
        return runningCts;
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
                detail: new Dictionary<string, object?> { ["execution_id"] = record.ExecutionId },
                remedy: new[] { "Restart Revit to recover this instance; a fresh instance_id will be issued on reconnect." });

            record.MarkUnrecoverable(now, diagnostic);
            _instanceUnrecoverable = true;

            // Fourth review finding: deliberately do NOT remove the dictionary entry here. Removing it was
            // this method's original behavior, but that's exactly the same mistake ApplyCancellation's
            // Pending branch made and was fixed for (see its doc comment): at grace expiry the script may
            // still be running -- that's the definition of grace expiry -- so removing the entry would make
            // GetCancellationToken start silently returning an uncancelled CancellationToken.None for an
            // execution that is in fact still running and was in fact cancelled. Cancel() was already
            // called whenever cancellation was first requested/escalated, so leaving the entry in place
            // costs nothing but keeps GetCancellationToken accurate for as long as anything might still
            // call it. This class never Dispose()s a CancellationTokenSource (see the class-level note), so
            // there's no resource cost to leaving it tracked -- an entry is now removed only on a clean
            // terminal completion (ClearActiveIfMatches), the one case where nothing will ever need the
            // token again.
        }
    }

    public ExecutionRecord? Poll(string executionId)
    {
        lock (_lock)
        {
            _ringBuffer.TryGet(executionId, out var record);
            return record;
        }
    }

    /// <summary>Caller must already hold <see cref="_lock"/>. Used by a normal terminal completion (the execution is genuinely done, nothing will ever need its token again), so it's safe to stop tracking the source here -- unlike <see cref="ApplyCancellation"/>'s Pending branch, which deliberately leaves it tracked.</summary>
    private void ClearActiveIfMatches(string executionId)
    {
        if (_active?.ExecutionId == executionId)
        {
            _active = null;
        }

        _cancellationSources.Remove(executionId);
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
            ["execution_id"] = record.ExecutionId,
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
    private static DiagnosticRecord UnknownExecutionDiagnostic(string executionId, string attemptedTransition) => DiagnosticRecord.Create(
        DiagnosticSeverity.Warning,
        "execution-transition-unknown-execution-id",
        DiagnosticSource.Execution,
        $"execution {executionId} was not found (most likely evicted from the ring buffer) by the time " +
        $"'{attemptedTransition}' arrived; the late transition was ignored.",
        detail: new Dictionary<string, object?>
        {
            ["execution_id"] = executionId,
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
