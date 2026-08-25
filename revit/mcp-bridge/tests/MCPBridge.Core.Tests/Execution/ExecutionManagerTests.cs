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
    public void CheckMaxDuration_ElapsedWhileStillPending_AutoRequestsCancellation()
    {
        // PRD §06 headline case for max_duration_ms: a script stuck behind a modal Revit dialog before
        // Execute() ever runs (still Pending) must still time out -- previously CheckMaxDuration only
        // matched { Status: Running }, so a Pending execution could sit forever.
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", maxDurationMs: 1000, now).Record!;
        Assert.Equal(ExecutionStatus.Pending, record.Status);

        manager.CheckMaxDuration(now.AddMilliseconds(1500));

        Assert.NotNull(record.CancellationRequestedAt);
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
    public void MarkRunning_RacedByGraceExpiry_DoesNotThrow()
    {
        // A Pending execution can be cancelled (and its grace period can expire) before it ever reaches
        // Execute() -- MarkRunning arriving after that must not throw either.
        var manager = NewManager();
        var now = DateTimeOffset.UtcNow;
        var record = manager.Start("// script", 600_000, now).Record!;
        manager.RequestCancellation(record.ExecutionId, now);
        manager.CheckGraceExpiry(now.AddSeconds(10));
        Assert.Equal(ExecutionStatus.Unrecoverable, record.Status);

        var diagnostic = manager.MarkRunning(record.ExecutionId, now.AddSeconds(10.1));

        Assert.NotNull(diagnostic);
        Assert.Equal(ExecutionStatus.Unrecoverable, record.Status);
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
