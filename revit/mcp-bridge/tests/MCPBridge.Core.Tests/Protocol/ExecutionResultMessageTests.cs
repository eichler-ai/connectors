using System;
using System.Text.Json;
using MCPBridge.Core.Diagnostics;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Protocol;
using Xunit;

namespace MCPBridge.Core.Tests.Protocol;

public class ExecutionResultMessageTests
{
    private static readonly JsonElement Id = JsonSerializer.SerializeToElement(1);
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void FromRecord_Pending_HasPendingStatusAndNoOutput()
    {
        var record = ExecutionRecord.CreatePending("exec-1", "1+1", 600_000, Now);

        var json = ExecutionResultMessage.FromRecord(Id, record);

        Assert.Contains("\"status\":\"pending\"", json);
        Assert.Contains("\"execution_id\":\"exec-1\"", json);
        Assert.DoesNotContain("\"output\"", json);
        Assert.DoesNotContain("\"notices\"", json);
        Assert.DoesNotContain("\"error\"", json);
    }

    [Fact]
    public void FromRecord_Completed_CarriesMutationsInSnakeCase_AndOmitsThemWhenAbsent()
    {
        // #146 Phase 2. The Go side's execution.Result reads exactly these names; keep the two in step.
        var record = ExecutionRecord.CreatePending("exec-1", "1+1", 600_000, Now);
        record.MarkRunning(Now);
        record.MarkCompleted(Now, "ok", null, Array.Empty<DiagnosticRecord>(), mutations: new MutationReport(
            created: 2, modified: 1, deleted: 0,
            byCategory: new Dictionary<string, CategoryTally> { ["Walls"] = new CategoryTally(2, 0), ["Levels"] = new CategoryTally(0, 1) },
            truncated: false));

        var json = ExecutionResultMessage.FromRecord(Id, record);

        Assert.Contains("\"mutations\":{\"created\":2,\"modified\":1,\"deleted\":0,\"by_category\":{\"Walls\":{\"created\":2,\"modified\":0},\"Levels\":{\"created\":0,\"modified\":1}},\"truncated\":false}", json);

        var readOnly = ExecutionRecord.CreatePending("exec-2", "1+1", 600_000, Now);
        readOnly.MarkRunning(Now);
        readOnly.MarkCompleted(Now, "ok", null, Array.Empty<DiagnosticRecord>());
        Assert.DoesNotContain("mutations", ExecutionResultMessage.FromRecord(Id, readOnly));
    }

    [Fact]
    public void FromRecord_Running_HasRunningStatus()
    {
        var record = ExecutionRecord.CreatePending("exec-1", "1+1", 600_000, Now);
        record.MarkRunning(Now);

        var json = ExecutionResultMessage.FromRecord(Id, record);

        Assert.Contains("\"status\":\"running\"", json);
    }

    [Fact]
    public void FromRecord_Success_KeepsStdOutAndReturnValueInSeparateFields()
    {
        var record = ExecutionRecord.CreatePending("exec-1", "1+1", 600_000, Now);
        record.MarkRunning(Now);
        record.MarkCompleted(Now, result: "2", stdOut: "hello\n", notices: Array.Empty<DiagnosticRecord>());

        var json = ExecutionResultMessage.FromRecord(Id, record);

        Assert.Contains("\"status\":\"success\"", json);
        // Issue #117: these were one folded field, so Revit's own console writes during a run
        // ("PlayerServer:Warning:No subscriber registered.") landed ahead of the script's returned
        // value with nothing marking the boundary. output is stdout verbatim -- PRD §06's documented
        // mapping, trailing newline and all, since trimming it was only ever in service of the fold.
        Assert.Contains("\"output\":\"hello\\n\"", json);
        Assert.Contains("\"return_value\":\"2\"", json);
    }

    [Fact]
    public void FromRecord_Success_NoStdOut_OmitsOutputButKeepsReturnValue()
    {
        var record = ExecutionRecord.CreatePending("exec-1", "1+1", 600_000, Now);
        record.MarkRunning(Now);
        record.MarkCompleted(Now, result: "2", stdOut: "", notices: Array.Empty<DiagnosticRecord>());

        var json = ExecutionResultMessage.FromRecord(Id, record);

        Assert.DoesNotContain("\"output\"", json);
        Assert.Contains("\"return_value\":\"2\"", json);
    }

    [Fact]
    public void FromRecord_Success_NullReturnValueAndNoStdOut_OmitsBoth()
    {
        var record = ExecutionRecord.CreatePending("exec-1", "Console.Write(1)", 600_000, Now);
        record.MarkRunning(Now);
        record.MarkCompleted(Now, result: null, stdOut: "", notices: Array.Empty<DiagnosticRecord>());

        var json = ExecutionResultMessage.FromRecord(Id, record);

        Assert.DoesNotContain("\"output\"", json);
        Assert.DoesNotContain("\"return_value\"", json);
    }

    /// <summary>
    /// The pairing that made issue #117 hard to see from the agent's side: stdout the script never wrote.
    /// Revit writes to the process console while a script runs, ScriptConsoleCapture correctly captures it,
    /// and folded into one field it read as part of the answer. Nothing about the split is worth much if
    /// the noisy half can still reach return_value.
    /// </summary>
    [Fact]
    public void FromRecord_Success_RevitsOwnConsoleChatterStaysOutOfReturnValue()
    {
        var record = ExecutionRecord.CreatePending("exec-1", "...", 600_000, Now);
        record.MarkRunning(Now);
        record.MarkCompleted(
            Now,
            result: @"C:\dev\fixtures\ProjectFresh.rvt",
            stdOut: "PlayerServer:Warning:No subscriber registered.\nPlayerServer:Warning:No subscriber registered.\n",
            notices: Array.Empty<DiagnosticRecord>());

        var json = ExecutionResultMessage.FromRecord(Id, record);
        using var parsed = JsonDocument.Parse(json);
        var result = parsed.RootElement.GetProperty("result");

        Assert.Equal(@"C:\dev\fixtures\ProjectFresh.rvt", result.GetProperty("return_value").GetString());
        Assert.DoesNotContain("PlayerServer", result.GetProperty("return_value").GetString()!);
        Assert.Contains("PlayerServer", result.GetProperty("output").GetString()!);
    }

    /// <summary>
    /// FromRecord reads ExecutionRecord.Result unconditionally, relying on MarkCompleted being its only
    /// writer. That invariant lives in another type, so it gets pinned from this side too: an errored
    /// run's stdout must not reappear as something the script returned.
    /// </summary>
    [Fact]
    public void FromRecord_Error_OmitsReturnValueEvenWithStdOut()
    {
        var record = ExecutionRecord.CreatePending("exec-1", "throw null;", 600_000, Now);
        record.MarkRunning(Now);
        var error = DiagnosticRecord.Create(DiagnosticSeverity.Error, "script-execution-failed", DiagnosticSource.Execution, "boom.", null, null);
        record.MarkError(Now, error, stdOut: "wrote this before throwing");

        var json = ExecutionResultMessage.FromRecord(Id, record);

        Assert.Contains("\"output\":\"wrote this before throwing\"", json);
        Assert.DoesNotContain("\"return_value\"", json);
    }

    [Fact]
    public void FromRecord_Success_WithNotices_IncludesNoticesArray()
    {
        var record = ExecutionRecord.CreatePending("exec-1", "1+1", 600_000, Now);
        record.MarkRunning(Now);
        var notice = DiagnosticRecord.Create(DiagnosticSeverity.Warning, "some-warning", DiagnosticSource.Execution, "a warning occurred.", null, null);
        record.MarkCompleted(Now, result: null, stdOut: "", notices: new[] { notice });

        var json = ExecutionResultMessage.FromRecord(Id, record);

        Assert.Contains("\"notices\":[{", json);
        Assert.Contains("\"code\":\"some-warning\"", json);
    }

    [Fact]
    public void FromRecord_Error_MapsToStatusError_AndIncludesErrorField()
    {
        var record = ExecutionRecord.CreatePending("exec-1", "throw", 600_000, Now);
        record.MarkRunning(Now);
        var error = DiagnosticRecord.Create(DiagnosticSeverity.Error, "script-execution-failed", DiagnosticSource.Execution, "boom.", null, null);
        record.MarkError(Now, error, stdOut: "partial output");

        var json = ExecutionResultMessage.FromRecord(Id, record);

        Assert.Contains("\"status\":\"error\"", json);
        Assert.Contains("\"error\":{", json);
        Assert.Contains("\"output\":\"partial output\"", json);
    }

    [Fact]
    public void FromRecord_Cancelled_MapsToStatusCancelled()
    {
        var record = ExecutionRecord.CreatePending("exec-1", "loop", 600_000, Now);
        record.MarkCancelled(Now, stdOut: null);

        var json = ExecutionResultMessage.FromRecord(Id, record);

        Assert.Contains("\"status\":\"cancelled\"", json);
        Assert.DoesNotContain("\"output\"", json);
    }

    [Fact]
    public void FromRecord_Unrecoverable_MapsToStatusUnrecoverable()
    {
        var record = ExecutionRecord.CreatePending("exec-1", "loop", 600_000, Now);
        record.MarkRunning(Now);
        var diagnostic = DiagnosticRecord.Create(DiagnosticSeverity.Error, "instance-unrecoverable", DiagnosticSource.Execution, "grace period expired.", null, null);
        record.MarkUnrecoverable(Now, diagnostic);

        var json = ExecutionResultMessage.FromRecord(Id, record);

        Assert.Contains("\"status\":\"unrecoverable\"", json);
        Assert.Contains("\"error\":{", json);
    }

    [Fact]
    public void Busy_HasBusyStatusAndExistingExecutionId_NoOutputOrError()
    {
        var json = ExecutionResultMessage.Busy(Id, "exec-existing");

        Assert.Contains("\"status\":\"busy\"", json);
        Assert.Contains("\"execution_id\":\"exec-existing\"", json);
        Assert.DoesNotContain("\"output\"", json);
        Assert.DoesNotContain("\"error\"", json);
    }

    [Fact]
    public void FromInstanceUnrecoverable_HasUnrecoverableStatus_EmptyExecutionId_AndDiagnostic()
    {
        var diagnostic = DiagnosticRecord.Create(DiagnosticSeverity.Error, "instance-unrecoverable", DiagnosticSource.Execution, "this instance is unrecoverable.", null, null);

        var json = ExecutionResultMessage.FromInstanceUnrecoverable(Id, diagnostic);

        Assert.Contains("\"status\":\"unrecoverable\"", json);
        Assert.Contains("\"execution_id\":\"\"", json);
        Assert.Contains("\"error\":{", json);
    }

    [Fact]
    public void FromRecord_WithExtraNotices_MergesIntoWireNotices_WithoutMutatingRecord()
    {
        var record = ExecutionRecord.CreatePending("exec-1", "1+1", 600_000, Now);
        var extra = DiagnosticRecord.Create(DiagnosticSeverity.Info, "window-inventory-timeout-fallback", DiagnosticSource.Dialogs, "possible blocking window.", null, null);

        var json = ExecutionResultMessage.FromRecord(Id, record, new[] { extra });

        Assert.Contains("\"code\":\"window-inventory-timeout-fallback\"", json);
        Assert.Empty(record.Notices); // ephemeral -- never written back onto the record
    }

    [Fact]
    public void ToJson_IsSingleLine_SafeForNdjsonFraming()
    {
        var record = ExecutionRecord.CreatePending("exec-1", "1+1", 600_000, Now);
        var json = ExecutionResultMessage.FromRecord(Id, record);

        Assert.DoesNotContain("\n", json);
    }
}
