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
    public void FromRecord_Running_HasRunningStatus()
    {
        var record = ExecutionRecord.CreatePending("exec-1", "1+1", 600_000, Now);
        record.MarkRunning(Now);

        var json = ExecutionResultMessage.FromRecord(Id, record);

        Assert.Contains("\"status\":\"running\"", json);
    }

    [Fact]
    public void FromRecord_Success_MapsToStatusSuccess_AndComposesStdOutAndReturnValue()
    {
        var record = ExecutionRecord.CreatePending("exec-1", "1+1", 600_000, Now);
        record.MarkRunning(Now);
        record.MarkCompleted(Now, result: 2, stdOut: "hello\n", notices: Array.Empty<DiagnosticRecord>());

        var json = ExecutionResultMessage.FromRecord(Id, record);

        Assert.Contains("\"status\":\"success\"", json);
        // Output field composition: StdOut is PRD §06's documented mapping ("stdout captured into the
        // result"); the script's own return value (2) is appended after it since Result has no separate
        // slot for it -- see ExecutionResultMessage's own doc comment for the full reasoning.
        Assert.Contains("\"output\":\"hello\\n\\n2\"", json);
    }

    [Fact]
    public void FromRecord_Success_NoStdOut_OutputIsJustTheReturnValue()
    {
        var record = ExecutionRecord.CreatePending("exec-1", "1+1", 600_000, Now);
        record.MarkRunning(Now);
        record.MarkCompleted(Now, result: 2, stdOut: "", notices: Array.Empty<DiagnosticRecord>());

        var json = ExecutionResultMessage.FromRecord(Id, record);

        Assert.Contains("\"output\":\"2\"", json);
    }

    [Fact]
    public void FromRecord_Success_NullReturnValueAndNoStdOut_OmitsOutput()
    {
        var record = ExecutionRecord.CreatePending("exec-1", "Console.Write(1)", 600_000, Now);
        record.MarkRunning(Now);
        record.MarkCompleted(Now, result: null, stdOut: "", notices: Array.Empty<DiagnosticRecord>());

        var json = ExecutionResultMessage.FromRecord(Id, record);

        Assert.DoesNotContain("\"output\"", json);
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
