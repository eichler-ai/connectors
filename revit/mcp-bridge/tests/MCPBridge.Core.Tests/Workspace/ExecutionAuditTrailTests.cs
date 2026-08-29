using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MCPBridge.Core.Diagnostics;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Workspace;
using Xunit;

namespace MCPBridge.Core.Tests.Workspace;

/// <summary>
/// Pins the §09 audit trail (issue #13): the per-run scripts/logs pair's names and content shape,
/// the never-throws contract, and the retention sweep's age math -- all against temp roots and a
/// caller-supplied `now`, no real waits (the SKILL testing rules).
/// </summary>
public class ExecutionAuditTrailTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcpbridge-audit-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }

    private WorkspacePaths NewWorkspace(string documentId = "doc-1234567890abcdef") =>
        WorkspacePaths.Local(documentId, "inst-1", userProfileRoot: _root);

    private static readonly DateTimeOffset CompletedAt = new(2026, 8, 29, 12, 34, 56, 789, TimeSpan.Zero);

    [Fact]
    public void Record_SuccessfulRun_WritesTheScriptAndNdjsonPair()
    {
        var workspace = NewWorkspace();
        var notice = DiagnosticRecord.Create(
            DiagnosticSeverity.Warning, "dialog-auto-answered", DiagnosticSource.Dialogs,
            "a dialog was auto-answered", detail: null, remedy: null);
        var outcome = ScriptExecutionOutcome.Completed(
            returnValue: 42,
            stdOut: "",
            notices: new[] { notice },
            files: new[] { new PublishedFileRecord("view.png", "exports/view.png", PublishedFileRecord.StatusPublished, null) });

        ExecutionAuditTrail.Record(workspace, "exec-abc", "return 42;", outcome, CompletedAt, trace: null);

        var scriptFile = Assert.Single(Directory.GetFiles(workspace.Scripts));
        Assert.Equal("20260829-123456789-exec-abc.cs", Path.GetFileName(scriptFile));
        Assert.Equal("return 42;", File.ReadAllText(scriptFile));

        var logFile = Assert.Single(Directory.GetFiles(workspace.Logs));
        Assert.Equal("20260829-123456789-exec-abc.ndjson", Path.GetFileName(logFile));

        var lines = File.ReadAllLines(logFile).Where(l => l.Length > 0).ToArray();
        Assert.Equal(2, lines.Length); // the notice verbatim, then the terminal record

        using var noticeLine = JsonDocument.Parse(lines[0]);
        Assert.Equal("dialog-auto-answered", noticeLine.RootElement.GetProperty("code").GetString());
        Assert.Equal("warning", noticeLine.RootElement.GetProperty("severity").GetString());

        using var terminal = JsonDocument.Parse(lines[1]);
        Assert.Equal("execution-audit", terminal.RootElement.GetProperty("code").GetString());
        Assert.Equal("info", terminal.RootElement.GetProperty("severity").GetString());
        var detail = terminal.RootElement.GetProperty("detail");
        Assert.Equal("success", detail.GetProperty("status").GetString());
        Assert.Equal("exec-abc", detail.GetProperty("execution_id").GetString());
        Assert.Equal("view.png", detail.GetProperty("files")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Record_FailedRun_TerminalRecordCarriesTheExceptionIdentity()
    {
        var workspace = NewWorkspace();
        var outcome = ScriptExecutionOutcome.Failed(new InvalidOperationException("boom"), stdOut: "");

        ExecutionAuditTrail.Record(workspace, "exec-err", "throw;", outcome, CompletedAt, trace: null);

        var logFile = Assert.Single(Directory.GetFiles(workspace.Logs));
        var terminalLine = File.ReadAllLines(logFile).Last(l => l.Length > 0);
        using var terminal = JsonDocument.Parse(terminalLine);
        Assert.Equal("error", terminal.RootElement.GetProperty("severity").GetString());
        var detail = terminal.RootElement.GetProperty("detail");
        Assert.Equal("failed", detail.GetProperty("status").GetString());
        Assert.Equal("System.InvalidOperationException", detail.GetProperty("exception_type").GetString());
        Assert.Contains("boom", terminal.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void Record_UnwritableWorkspace_NeverThrows_AndTraces()
    {
        // Make DocumentRoot's parent a FILE so every directory create/write under it fails.
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "RevitMCPExchange"), "not a directory");
        var workspace = NewWorkspace();
        var traces = new List<string>();

        var exception = Xunit.Record.Exception(() => ExecutionAuditTrail.Record(
            workspace, "exec-x", "1;", ScriptExecutionOutcome.Completed(null, ""), CompletedAt, traces.Add));

        Assert.Null(exception);
        Assert.Contains(traces, t => t.Contains("exec-x"));
    }

    [Fact]
    public void Sweep_DeletesAgedAuditAndTmp_KeepsFreshOnes_NeverTouchesImportsOrExports()
    {
        var workspace = NewWorkspace();
        var now = DateTimeOffset.UtcNow;
        var aged = now.UtcDateTime.AddDays(-15);
        var fresh = now.UtcDateTime.AddDays(-1);

        string Write(string dir, string name, DateTime stampUtc)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, stampUtc);
            return path;
        }

        var agedLog = Write(workspace.Logs, "old.ndjson", aged);
        var freshLog = Write(workspace.Logs, "new.ndjson", fresh);
        var agedScript = Write(workspace.Scripts, "old.cs", aged);
        var agedImport = Write(workspace.Imports, "user-upload.csv", aged);
        var agedExport = Write(workspace.Exports, "user-output.png", aged);

        var agedTmp = workspace.Tmp();
        Write(agedTmp, "scratch.bin", aged);
        Directory.SetLastWriteTimeUtc(agedTmp, aged);

        ExecutionAuditTrail.Sweep(workspace.ExchangeRoot, now, TimeSpan.FromDays(14), trace: null);

        Assert.False(File.Exists(agedLog), "aged log should be swept");
        Assert.False(File.Exists(agedScript), "aged script should be swept");
        Assert.False(Directory.Exists(agedTmp), "aged tmp/<instance> dir should be swept");
        Assert.True(File.Exists(freshLog), "fresh log must survive");
        Assert.True(File.Exists(agedImport), "imports/ is user-owned and never swept, whatever its age");
        Assert.True(File.Exists(agedExport), "exports/ is user-owned and never swept, whatever its age");
    }

    [Fact]
    public void Record_PathSeparatorsInAnExecutionId_CannotEscapeTheWorkspace()
    {
        // The broker mints well-formed ids, but execution_id arrives over the wire, and the wire --
        // not the broker's good behavior -- is the §10 trust boundary. A traversal-shaped id must
        // land (mangled) INSIDE the workspace, never outside it.
        var workspace = NewWorkspace();
        var hostile = "..\\..\\escape/..\\evil";

        ExecutionAuditTrail.Record(workspace, hostile, "1;", ScriptExecutionOutcome.Completed(null, ""), CompletedAt, trace: null);

        // The decisive property is WHERE the files landed: directly inside the workspace's own
        // scripts/ and logs/ dirs (separators mangled into the name -- a literal ".." WITHIN a
        // file name is harmless; a path segment would not have been), and nothing anywhere above.
        var scriptFile = Assert.Single(Directory.GetFiles(workspace.Scripts));
        Assert.Equal(workspace.Scripts, Path.GetDirectoryName(scriptFile));
        var logFile = Assert.Single(Directory.GetFiles(workspace.Logs));
        Assert.Equal(workspace.Logs, Path.GetDirectoryName(logFile));
        Assert.Empty(Directory.GetFiles(_root)); // nothing landed at the substitute profile root
    }

    [Fact]
    public void Sweep_MissingRootOrLockedEntries_NeverThrows()
    {
        var exception = Xunit.Record.Exception(() => ExecutionAuditTrail.Sweep(
            Path.Combine(_root, "does-not-exist"), DateTimeOffset.UtcNow, TimeSpan.FromDays(14), trace: null));

        Assert.Null(exception);
    }
}
