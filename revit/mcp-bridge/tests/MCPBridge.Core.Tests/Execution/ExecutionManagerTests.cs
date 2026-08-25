using System;
using System.Threading;
using MCPBridge.Core.Diagnostics;
using MCPBridge.Core.Execution;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

public class ExecutionManagerTests
{
    private static ExecutionManager NewManager() =>
        new(new ExecutionRingBuffer(capacity: 50, retention: TimeSpan.FromMinutes(10)), gracePeriod: TimeSpan.FromSeconds(5));

    [Fact]
    public void Start_WhenIdle_ReturnsNewPendingExecution()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;

        var outcome = manager.Start("// script", maxDurationMs: 600_000, now);

        Assert.Equal(ExecuteOutcomeKind.Started, outcome.Kind);
        Assert.NotNull(outcome.Record);
        Assert.Equal(ExecutionStatus.Pending, outcome.Record!.Status);
    }

    [Fact]
    public void Start_WhileAnExecutionIsActive_ReturnsBusyPointingAtExistingId()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var first = manager.Start("// first", 600_000, now).Record!;

        var second = manager.Start("// second", 600_000, now);

        Assert.Equal(ExecuteOutcomeKind.Busy, second.Kind);
        Assert.Equal(first.ExecutionId, second.Record!.ExecutionId);
    }

    [Fact]
    public void Start_WhilePending_IsStillBusy_NotJustWhileRunning()
    {
        // pending (queued, ExternalEvent not fired yet) is a distinct state from running,
        // but both occupy the instance for the purposes of a second execute_script (PRD §06).
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var first = manager.Start("// first", 600_000, now).Record!;
        Assert.Equal(ExecutionStatus.Pending, first.Status);

        var second = manager.Start("// second", 600_000, now);

        Assert.Equal(ExecuteOutcomeKind.Busy, second.Kind);
    }

    [Fact]
    public void MarkRunning_TransitionsPendingToRunning()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;

        manager.MarkRunning(record.ExecutionId, now.AddMilliseconds(10));

        Assert.Equal(ExecutionStatus.Running, record.Status);
    }

    [Fact]
    public void CompleteSuccess_FreesInstance_ForNextExecution()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);

        manager.CompleteSuccess(record.ExecutionId, now, result: 42, stdOut: null, notices: Array.Empty<DiagnosticRecord>());

        Assert.Equal(ExecutionStatus.Completed, record.Status);
        var next = manager.Start("// next", 600_000, now);
        Assert.Equal(ExecuteOutcomeKind.Started, next.Kind);
    }

    [Fact]
    public void CompleteError_FreesInstance()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);

        var diagnostic = DiagnosticRecord.Create(
            DiagnosticSeverity.Error, "script-exception", DiagnosticSource.Execution,
            $"execution {record.ExecutionId} threw a NullReferenceException", null, null);
        manager.CompleteError(record.ExecutionId, now, diagnostic, stdOut: null);

        Assert.Equal(ExecutionStatus.Error, record.Status);
        Assert.Same(diagnostic, record.Error);

        var next = manager.Start("// next", 600_000, now);
        Assert.Equal(ExecuteOutcomeKind.Started, next.Kind);
    }

    [Fact]
    public void RequestCancellation_UnknownId_ReturnsNotFound()
    {
        var manager = NewManager();

        var result = manager.RequestCancellation(Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(CancellationRequestOutcome.NotFound, result);
    }

    [Fact]
    public void RequestCancellation_AlreadyTerminal_ReturnsAlreadyTerminal()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);
        manager.CompleteSuccess(record.ExecutionId, now, null, null, Array.Empty<DiagnosticRecord>());

        var result = manager.RequestCancellation(record.ExecutionId, now);

        Assert.Equal(CancellationRequestOutcome.AlreadyTerminal, result);
    }

    [Fact]
    public void RequestCancellation_StillPending_ResolvesDirectlyToCancelled_NotGraceFlow()
    {
        // Second review, Fix 4: cancelling a Pending execution (e.g. execute_script called while a modal
        // Revit dialog is blocking the idle loop) must resolve directly to Cancelled -- there's nothing
        // running that needs to cooperatively/forcibly stop, so it must not merely stamp
        // CancellationRequestedAt and wait on the grace-timer's "didn't stop -> Unrecoverable" escalation.
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        Assert.Equal(ExecutionStatus.Pending, record.Status);

        var result = manager.RequestCancellation(record.ExecutionId, now.AddSeconds(1));

        Assert.Equal(CancellationRequestOutcome.Acknowledged, result);
        Assert.Equal(ExecutionStatus.Cancelled, record.Status);
        Assert.False(manager.IsInstanceUnrecoverable);

        // Frees the instance slot immediately -- no need to wait out a grace period for a Pending cancel.
        var next = manager.Start("// next", 600_000, now.AddSeconds(2));
        Assert.Equal(ExecuteOutcomeKind.Started, next.Kind);
    }

    [Fact]
    public void RequestCancellation_Active_Acknowledged_AndRecordsRequestTime()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);

        var result = manager.RequestCancellation(record.ExecutionId, now.AddSeconds(1));

        Assert.Equal(CancellationRequestOutcome.Acknowledged, result);
        Assert.Equal(now.AddSeconds(1), record.CancellationRequestedAt);
    }

    [Fact]
    public void CompleteCancelled_WhenScriptCooperates_ResolvesToCancelled_NotUnrecoverable()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);
        manager.RequestCancellation(record.ExecutionId, now);

        manager.CompleteCancelled(record.ExecutionId, now.AddSeconds(1), stdOut: null);

        Assert.Equal(ExecutionStatus.Cancelled, record.Status);
        Assert.False(manager.IsInstanceUnrecoverable);
    }

    [Fact]
    public void CheckMaxDuration_ElapsedPastCeiling_AutoRequestsCancellation()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", maxDurationMs: 1000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);

        manager.CheckMaxDuration(now.AddMilliseconds(1500));

        Assert.NotNull(record.CancellationRequestedAt);
    }

    [Fact]
    public void CheckMaxDuration_NotYetElapsed_DoesNotCancel()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", maxDurationMs: 10_000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);

        manager.CheckMaxDuration(now.AddMilliseconds(500));

        Assert.Null(record.CancellationRequestedAt);
    }

    [Fact]
    public void CheckMaxDuration_IsIdempotent_DoesNotResetRequestTime()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", maxDurationMs: 1000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);

        manager.CheckMaxDuration(now.AddMilliseconds(1500));
        var firstRequestTime = record.CancellationRequestedAt;
        manager.CheckMaxDuration(now.AddMilliseconds(2500));

        Assert.Equal(firstRequestTime, record.CancellationRequestedAt);
    }

    [Fact]
    public void CheckGraceExpiry_ScriptDidNotStop_MarksUnrecoverable_AndSticksInstanceFlag()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);
        manager.RequestCancellation(record.ExecutionId, now);

        manager.CheckGraceExpiry(now.AddSeconds(6));

        Assert.Equal(ExecutionStatus.Unrecoverable, record.Status);
        Assert.True(manager.IsInstanceUnrecoverable);
    }

    [Fact]
    public void CheckGraceExpiry_BeforeGraceElapses_DoesNothing()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);
        manager.RequestCancellation(record.ExecutionId, now);

        manager.CheckGraceExpiry(now.AddSeconds(2));

        Assert.Equal(ExecutionStatus.Running, record.Status);
        Assert.False(manager.IsInstanceUnrecoverable);
    }

    [Fact]
    public void CheckGraceExpiry_ScriptAlreadyCompletedBeforeGrace_DoesNotOverwriteTerminalState()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);
        manager.RequestCancellation(record.ExecutionId, now);
        manager.CompleteCancelled(record.ExecutionId, now.AddSeconds(1), stdOut: null);

        manager.CheckGraceExpiry(now.AddSeconds(10));

        Assert.Equal(ExecutionStatus.Cancelled, record.Status);
        Assert.False(manager.IsInstanceUnrecoverable);
    }

    [Fact]
    public void OnceUnrecoverable_FurtherStartCalls_ReturnInstanceUnrecoverable_NotBusy()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);
        manager.RequestCancellation(record.ExecutionId, now);
        manager.CheckGraceExpiry(now.AddSeconds(6));

        var outcome = manager.Start("// another", 600_000, now.AddSeconds(7));

        Assert.Equal(ExecuteOutcomeKind.InstanceUnrecoverable, outcome.Kind);
        Assert.NotNull(outcome.Diagnostic);
        Assert.NotEmpty(outcome.Diagnostic!.Remedy);
    }

    [Fact]
    public void Poll_ReturnsRecordFromRingBuffer_RegardlessOfActiveSlot()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);
        manager.CompleteSuccess(record.ExecutionId, now, null, null, Array.Empty<DiagnosticRecord>());

        var polled = manager.Poll(record.ExecutionId);

        Assert.NotNull(polled);
        Assert.Equal(ExecutionStatus.Completed, polled!.Status);
    }

    [Fact]
    public void Poll_UnknownExecutionId_ReturnsNull_NotHang()
    {
        var manager = NewManager();
        Assert.Null(manager.Poll(Guid.NewGuid()));
    }

    // --- Fix 3: CheckMaxDuration must fire for Pending, not just Running ---

    [Fact]
    public void CheckMaxDuration_ElapsedWhileStillPending_ResolvesDirectlyToCancelled()
    {
        // PRD §06 headline case for max_duration_ms: a script stuck behind a modal Revit dialog before
        // Execute() ever runs (still Pending) must still time out -- previously CheckMaxDuration only
        // matched { Status: Running }, so a Pending execution could sit forever.
        //
        // Second review, Fix 4: a Pending execution has nothing running that needs to cooperatively/
        // forcibly stop, so it must resolve directly to Cancelled here, not merely stamp
        // CancellationRequestedAt and escalate through the grace-timer flow to instance-Unrecoverable --
        // that flow is for a script actually in flight.
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", maxDurationMs: 1000, now).Record!;
        Assert.Equal(ExecutionStatus.Pending, record.Status);

        manager.CheckMaxDuration(now.AddMilliseconds(1500));

        Assert.Equal(ExecutionStatus.Cancelled, record.Status);
        Assert.False(manager.IsInstanceUnrecoverable);

        // The instance slot must be free again -- a Pending cancel is a clean resolution, not a busy wait.
        var next = manager.Start("// next", 600_000, now.AddMilliseconds(1600));
        Assert.Equal(ExecuteOutcomeKind.Started, next.Kind);
    }

    [Fact]
    public void CheckMaxDuration_ElapsedWhileStillPending_ActuallyCancelsTheToken()
    {
        // Third review finding: a still-Pending record's CancellationTokenSource was previously left
        // untouched (never Cancel()'d) and its dictionary entry was removed, so GetCancellationToken
        // silently started returning CancellationToken.None -- permanently unset -- for an execution whose
        // ExternalEventBridge work item is still going to fire eventually. That defeated the BridgeHost.cs
        // TODO's mandated check ("bail out if IsCancellationRequested"): a Pending execution cancelled here
        // must still report IsCancellationRequested == true once its queued work item finally runs.
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", maxDurationMs: 1000, now).Record!;
        var token = manager.GetCancellationToken(record.ExecutionId);
        Assert.False(token.IsCancellationRequested);

        manager.CheckMaxDuration(now.AddMilliseconds(1500));

        Assert.Equal(ExecutionStatus.Cancelled, record.Status);
        Assert.True(token.IsCancellationRequested);
        // The token must still be resolvable (not thrown from a disposed source) after the cancel.
        Assert.True(manager.GetCancellationToken(record.ExecutionId).IsCancellationRequested);
    }

    [Fact]
    public void RequestCancellation_WhileStillPending_ActuallyCancelsTheToken()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", maxDurationMs: 600_000, now).Record!;
        var token = manager.GetCancellationToken(record.ExecutionId);

        var outcome = manager.RequestCancellation(record.ExecutionId, now);

        Assert.Equal(CancellationRequestOutcome.Acknowledged, outcome);
        Assert.Equal(ExecutionStatus.Cancelled, record.Status);
        Assert.True(token.IsCancellationRequested);
        // The dictionary entry must still be there too (not just the pre-captured token) -- a later,
        // independent GetCancellationToken call for this id must keep reporting cancelled.
        Assert.True(manager.GetCancellationToken(record.ExecutionId).IsCancellationRequested);
    }

    [Fact]
    public void RequestCancellation_ScriptRegisteredCallbackThrows_DoesNotPropagate_StillCancelsAndAcknowledges()
    {
        // Fourth review finding: CancellationTokenSource.Cancel() runs registered callbacks synchronously
        // and rethrows any callback failure as an AggregateException -- distinct from (and not fixed by)
        // this class no longer Dispose()ing its sources. ScriptGlobals.CancellationToken is exposed by name
        // into arbitrary script scope, so a script's own registered callback throwing must never propagate
        // out of RequestCancellation/CheckMaxDuration, both of which can be driven from Revit's UI thread.
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", maxDurationMs: 600_000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);
        var token = manager.GetCancellationToken(record.ExecutionId);
        token.Register(() => throw new InvalidOperationException("script cleanup callback boom"));

        var ex = Record.Exception(() => manager.RequestCancellation(record.ExecutionId, now));

        Assert.Null(ex);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(ExecutionStatus.Running, record.Status); // cancellation requested, not yet resolved
        Assert.NotNull(record.CancellationRequestedAt);
    }

    [Fact]
    public void CheckMaxDuration_ElapsedWhileStillPending_GraceExpiryNeverEscalatesIt()
    {
        // Even if CheckGraceExpiry is later called against a stale `now`, a Pending-cancelled execution
        // must never be escalated to Unrecoverable -- it already resolved to Cancelled directly and is
        // therefore terminal (and, separately, no longer the active execution once a later one starts).
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", maxDurationMs: 1000, now).Record!;

        manager.CheckMaxDuration(now.AddMilliseconds(1500));
        Assert.Equal(ExecutionStatus.Cancelled, record.Status);

        manager.CheckGraceExpiry(now.AddSeconds(30));

        Assert.Equal(ExecutionStatus.Cancelled, record.Status);
        Assert.False(manager.IsInstanceUnrecoverable);
    }

    [Fact]
    public void CheckMaxDuration_Pending_NotYetElapsed_DoesNotCancel()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", maxDurationMs: 10_000, now).Record!;

        manager.CheckMaxDuration(now.AddMilliseconds(500));

        Assert.Null(record.CancellationRequestedAt);
    }

    [Fact]
    public void CheckMaxDuration_MeasuredFromCreation_NotFromStartedRunning()
    {
        // A Pending execution that later starts Running still budgets from when it was queued
        // (CreatedAt), not from whenever it happened to reach Running -- max_duration_ms is the whole
        // execute_script call's budget, queue wait included.
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", maxDurationMs: 1000, now).Record!;

        manager.MarkRunning(record.ExecutionId, now.AddMilliseconds(900));
        manager.CheckMaxDuration(now.AddMilliseconds(1100));

        Assert.NotNull(record.CancellationRequestedAt);
    }

    // --- Fix 4: finishing-path transitions must never throw when raced by the grace timer ---

    [Fact]
    public void CompleteSuccess_RacedByGraceExpiry_DoesNotThrow_ReturnsRaceDiagnostic()
    {
        // Simulates the race directly (PR #2 review, Fix 4): the grace timer flips the record to
        // Unrecoverable first; the script's own finishing path then arrives and must not throw
        // InvalidOperationException -- especially since in production this path can be invoked from
        // inside Revit's UI-thread Execute() callback, where an uncaught exception is a crash-class bug.
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);
        manager.RequestCancellation(record.ExecutionId, now);
        manager.CheckGraceExpiry(now.AddSeconds(10));
        Assert.Equal(ExecutionStatus.Unrecoverable, record.Status);

        var diagnostic = Record.Exception(() =>
            manager.CompleteSuccess(record.ExecutionId, now.AddSeconds(10.1), result: 1, stdOut: null, notices: Array.Empty<DiagnosticRecord>()));

        Assert.Null(diagnostic); // no exception thrown

        var raceDiagnostic = manager.CompleteSuccess(record.ExecutionId, now.AddSeconds(10.2), result: 1, stdOut: null, notices: Array.Empty<DiagnosticRecord>());
        Assert.NotNull(raceDiagnostic);
        Assert.Equal(DiagnosticSeverity.Warning, raceDiagnostic!.Severity);
        Assert.Equal("execution-transition-raced-terminal", raceDiagnostic.Code);
        Assert.Equal(ExecutionStatus.Unrecoverable, record.Status); // still terminal, not overwritten
    }

    [Fact]
    public void CompleteError_RacedByGraceExpiry_DoesNotThrow_LeavesRecordUnrecoverable()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);
        manager.RequestCancellation(record.ExecutionId, now);
        manager.CheckGraceExpiry(now.AddSeconds(10));

        var error = DiagnosticRecord.Create(DiagnosticSeverity.Error, "script-exception", DiagnosticSource.Execution, "boom", null, null);
        var diagnostic = manager.CompleteError(record.ExecutionId, now.AddSeconds(10.1), error, stdOut: null);

        Assert.NotNull(diagnostic);
        Assert.Equal(ExecutionStatus.Unrecoverable, record.Status);
        Assert.NotSame(error, record.Error); // the raced CompleteError's error was never applied
    }

    [Fact]
    public void CompleteCancelled_RacedByGraceExpiry_DoesNotThrow()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);
        manager.RequestCancellation(record.ExecutionId, now);
        manager.CheckGraceExpiry(now.AddSeconds(10));

        var diagnostic = manager.CompleteCancelled(record.ExecutionId, now.AddSeconds(10.1), stdOut: null);

        Assert.NotNull(diagnostic);
        Assert.Equal(ExecutionStatus.Unrecoverable, record.Status);
    }

    [Fact]
    public void CompleteSuccess_UnknownExecutionId_DoesNotThrow_ReturnsDiagnostic()
    {
        // Second review, Fix 5: an execution_id Transition() can't find at all (e.g. evicted from the ring
        // buffer) must never throw -- especially since this path can be invoked from Revit's UI thread.
        var manager = NewManager();
        var unknownId = Guid.NewGuid();

        var diagnostic = Record.Exception(() =>
            manager.CompleteSuccess(unknownId, DateTimeOffset.UtcNow, result: 1, stdOut: null, notices: Array.Empty<DiagnosticRecord>()));
        Assert.Null(diagnostic); // no exception thrown

        var result = manager.CompleteSuccess(unknownId, DateTimeOffset.UtcNow, result: 1, stdOut: null, notices: Array.Empty<DiagnosticRecord>());
        Assert.NotNull(result);
        Assert.Equal(DiagnosticSeverity.Warning, result!.Severity);
        Assert.Equal("execution-transition-unknown-execution-id", result.Code);
    }

    [Fact]
    public void MarkRunning_UnknownExecutionId_DoesNotThrow_ReturnsDiagnostic()
    {
        var manager = NewManager();
        var unknownId = Guid.NewGuid();

        var diagnostic = manager.MarkRunning(unknownId, DateTimeOffset.UtcNow);

        Assert.NotNull(diagnostic);
        Assert.Equal("execution-transition-unknown-execution-id", diagnostic!.Code);
    }

    [Fact]
    public void MarkRunning_AfterPendingWasAlreadyCancelled_DoesNotThrow_ReturnsRaceDiagnostic()
    {
        // Second review, Fix 4 changed what this race looks like: a Pending execution's cancellation now
        // resolves directly to Cancelled (terminal) rather than escalating through the grace-timer flow to
        // Unrecoverable (that escalation is reserved for a script actually in flight, per Fix 4 -- a
        // Pending execution's own CancellationRequestedAt is never even stamped, so CheckGraceExpiry can no
        // longer race it into Unrecoverable the way this test previously simulated). What must still hold:
        // a MarkRunning() call that arrives after that cancellation (e.g. Revit's idle loop finally reaches
        // Execute() for a request that was already cancelled while queued) must not throw --
        // Transition()'s terminal check must intercept it before ExecutionRecord.MarkRunning's own
        // Pending-only precondition (RequireStatus(Pending)) would throw instead.
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        manager.RequestCancellation(record.ExecutionId, now);
        Assert.Equal(ExecutionStatus.Cancelled, record.Status);

        var thrown = Record.Exception(() => manager.MarkRunning(record.ExecutionId, now.AddSeconds(0.1)));
        Assert.Null(thrown);

        var diagnostic = manager.MarkRunning(record.ExecutionId, now.AddSeconds(0.2));

        Assert.NotNull(diagnostic);
        Assert.Equal("execution-transition-raced-terminal", diagnostic!.Code);
        Assert.Equal(ExecutionStatus.Cancelled, record.Status);
    }

    // --- Cancellation wiring: a real CancellationTokenSource per execution ---

    [Fact]
    public void RequestCancellation_Acknowledged_ActuallyCancelsTheToken()
    {
        // Previously ScriptGlobals.CancellationToken existed but nothing ever wired a real
        // CancellationTokenSource to it, so cancel_execution could not stop even a cooperative script.
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);
        var token = manager.GetCancellationToken(record.ExecutionId);
        Assert.False(token.IsCancellationRequested);

        manager.RequestCancellation(record.ExecutionId, now);

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void CheckMaxDuration_ElapsedWhileRunning_ActuallyCancelsTheToken()
    {
        // Second review, Fix 3: CheckMaxDuration's auto-cancel previously only stamped
        // CancellationRequestedAt on the record and never touched the CancellationTokenSource, so a
        // cooperative script polling its token never actually observed a max-duration timeout.
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", maxDurationMs: 1000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);
        var token = manager.GetCancellationToken(record.ExecutionId);
        Assert.False(token.IsCancellationRequested);

        manager.CheckMaxDuration(now.AddMilliseconds(1500));

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(ExecutionStatus.Running, record.Status); // still needs to cooperatively stop / hit grace
    }

    [Fact]
    public void GetCancellationToken_UnknownExecutionId_ReturnsNone_NotThrow()
    {
        var manager = NewManager();
        Assert.Equal(CancellationToken.None, manager.GetCancellationToken(Guid.NewGuid()));
    }

    [Fact]
    public void GetCancellationToken_ForFreshExecution_IsNotAlreadyCancelled()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;

        Assert.False(manager.GetCancellationToken(record.ExecutionId).IsCancellationRequested);
    }

    [Fact]
    public void CompleteSuccess_NormalPath_StillReturnsNullDiagnostic()
    {
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        manager.MarkRunning(record.ExecutionId, now);

        var diagnostic = manager.CompleteSuccess(record.ExecutionId, now, result: 42, stdOut: null, notices: Array.Empty<DiagnosticRecord>());

        Assert.Null(diagnostic);
        Assert.Equal(ExecutionStatus.Completed, record.Status);
    }
}
