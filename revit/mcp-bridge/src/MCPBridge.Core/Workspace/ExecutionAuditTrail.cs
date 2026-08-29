using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MCPBridge.Core.Diagnostics;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Protocol;

namespace MCPBridge.Core.Workspace;

/// <summary>
/// The per-execution audit trail PRD §09 always specified and issue #13 turned out to presume
/// (the directories were never built until this shipped): after every run that reached the script
/// executor, `scripts/&lt;utc-stamp&gt;-&lt;execution_id&gt;.cs` holds the verbatim script text and
/// `logs/&lt;utc-stamp&gt;-&lt;execution_id&gt;.ndjson` holds one PRD §01 diagnostic record per line
/// -- the run's notices verbatim, then one terminal record naming the status (with the exception
/// code/type on failure, and a per-file summary of everything Publish touched). Runs refused
/// BEFORE a document was resolved (document-not-found, no-active-document, cancelled-while-queued)
/// leave no audit entry: they touched nothing, their refusal is fully reported through the §01
/// error path, and -- decisively -- there is no routed document whose workspace could receive one.
///
/// Everything here is best-effort by hard contract: an audit failure must NEVER fail, slow, or
/// alter a run's outcome, so every entry point swallows and reports through the caller's trace
/// hook (the AddIn passes the connection-log writer) rather than throwing. §01's
/// observability-over-silence still holds -- the failure leaves a trace, just never in the run's
/// own result.
///
/// Retention (issue #13): a once-per-process background sweep, triggered from the first audited
/// completion rather than a timer service to own, deletes `logs/`/`scripts/` files and
/// `tmp/&lt;instance-id&gt;/` directories older than <see cref="RetentionDays"/> days across EVERY
/// document workspace under the exchange root. `imports/`/`exports/` are user-owned and never
/// touched (§09's retention-by-ownership split). `tmp/` cleanup additionally has no
/// on-document-close hook on purpose: DocumentClosed's args carry only Revit's internal integer id
/// -- the §09 identity (and the Title that now derives it) is unrecoverable once the document is
/// gone, and pairing the cancellable DocumentClosing event with Closed just to delete scratch a
/// few days earlier than the sweep would is machinery the overengineering test refuses.
/// </summary>
internal static class ExecutionAuditTrail
{
    internal const int RetentionDays = 14;

    private static int _sweepStarted;

    /// <summary>
    /// Writes one completed run's audit pair into <paramref name="workspace"/> and, on the first
    /// call in this process, kicks off the background retention sweep. Never throws.
    /// </summary>
    internal static void Record(
        WorkspacePaths workspace,
        string executionId,
        string scriptText,
        ScriptExecutionOutcome outcome,
        DateTimeOffset completedAtUtc,
        Action<string>? trace)
    {
        try
        {
            // Sortable, filesystem-safe, millisecond-distinct alongside the execution id -- two
            // runs can't collide (ids are unique) and a directory listing reads chronologically.
            // The id itself is SANITIZED before touching a path (fresh-eyes self-review): the
            // broker mints well-formed ids, but execution_id arrives over the wire, and the wire --
            // not the broker's good behavior -- is the §10 trust boundary; a path separator in an
            // id must never let an audit file land outside the workspace.
            var stamp = completedAtUtc.UtcDateTime.ToString("yyyyMMdd-HHmmssfff");
            var baseName = $"{stamp}-{SanitizeForFileName(executionId)}";

            File.WriteAllText(Path.Combine(workspace.Scripts, baseName + ".cs"), scriptText);

            var lines = new StringBuilder();
            foreach (var notice in outcome.Notices)
            {
                lines.AppendLine(JsonSerializer.Serialize(notice, WireJson.Compact));
            }

            lines.AppendLine(JsonSerializer.Serialize(TerminalRecord(executionId, outcome), WireJson.Compact));
            File.WriteAllText(Path.Combine(workspace.Logs, baseName + ".ndjson"), lines.ToString());
        }
        catch (Exception ex)
        {
            trace?.Invoke($"audit trail write failed for execution {executionId} (the run itself is unaffected): {ex.GetType().Name}: {ex.Message}");
        }

        StartRetentionSweepOncePerProcess(workspace, trace);
    }

    /// <summary>
    /// The one terminal §01 record per run: status, failure identity when there is one, and the
    /// per-file outcome of everything Publish touched -- so the log line a human (or agent) reads
    /// two weeks later answers "what happened" without the broker's ring buffer, which has long
    /// since evicted the run.
    /// </summary>
    private static DiagnosticRecord TerminalRecord(string executionId, ScriptExecutionOutcome outcome)
    {
        var status = outcome.WasCancelled ? "cancelled" : outcome.Success ? "success" : "failed";
        var detail = new Dictionary<string, object?>
        {
            ["execution_id"] = executionId,
            ["status"] = status,
        };
        if (outcome.Exception is { } exception)
        {
            detail["exception_type"] = exception.GetType().FullName;
        }

        if (outcome.Files.Count > 0)
        {
            detail["files"] = outcome.Files
                .Select(f => new Dictionary<string, object?> { ["name"] = f.Name, ["status"] = f.Status })
                .ToList();
        }

        var message = outcome.Exception is { } ex
            ? $"execution {executionId} {status}: {ex.GetType().Name}: {ex.Message}"
            : $"execution {executionId} {status} ({outcome.Files.Count} file(s) published)";

        return DiagnosticRecord.Create(
            outcome.Success || outcome.WasCancelled ? DiagnosticSeverity.Info : DiagnosticSeverity.Error,
            "execution-audit",
            DiagnosticSource.Execution,
            message,
            detail,
            remedy: null);
    }

    private static void StartRetentionSweepOncePerProcess(WorkspacePaths workspace, Action<string>? trace)
    {
        if (Interlocked.Exchange(ref _sweepStarted, 1) != 0)
        {
            return;
        }

        var exchangeRoot = workspace.ExchangeRoot;
        Task.Run(() => Sweep(exchangeRoot, DateTimeOffset.UtcNow, TimeSpan.FromDays(RetentionDays), trace));
    }

    /// <summary>
    /// Deletes aged audit files and tmp scratch across every document workspace under
    /// <paramref name="exchangeRoot"/>. Separated from the once-per-process trigger (and taking
    /// `now`/`retention` as parameters) so the age logic is tier-1 testable with no real waits.
    /// Per-entry try/catch: one locked file must not stop the rest of the sweep. Never throws.
    /// </summary>
    internal static void Sweep(string exchangeRoot, DateTimeOffset nowUtc, TimeSpan retention, Action<string>? trace)
    {
        try
        {
            if (!Directory.Exists(exchangeRoot))
            {
                return;
            }

            var cutoffUtc = nowUtc.UtcDateTime - retention;
            foreach (var documentRoot in Directory.EnumerateDirectories(exchangeRoot))
            {
                // Per-DIRECTORY failure isolation (PR review): each swept directory's enumeration
                // gets its own catch, so one ACL-broken logs/ traces and moves on instead of
                // abandoning the rest of the process's only sweep.
                foreach (var auditDir in new[] { Path.Combine(documentRoot, "logs"), Path.Combine(documentRoot, "scripts") })
                {
                    try
                    {
                        if (!Directory.Exists(auditDir))
                        {
                            continue;
                        }

                        foreach (var file in Directory.EnumerateFiles(auditDir))
                        {
                            TryDelete(file, cutoffUtc, trace);
                        }
                    }
                    catch (Exception ex)
                    {
                        trace?.Invoke($"audit retention sweep skipped '{auditDir}': {ex.GetType().Name}: {ex.Message}");
                    }
                }

                try
                {
                    var tmpRoot = Path.Combine(documentRoot, "tmp");
                    if (Directory.Exists(tmpRoot))
                    {
                        foreach (var instanceDir in Directory.EnumerateDirectories(tmpRoot))
                        {
                            TryDeleteDirectory(instanceDir, cutoffUtc, trace);
                        }
                    }
                }
                catch (Exception ex)
                {
                    trace?.Invoke($"audit retention sweep skipped tmp under '{documentRoot}': {ex.GetType().Name}: {ex.Message}");
                }

                // imports/ and exports/ are deliberately never visited -- user-owned (PRD §09).
            }
        }
        catch (Exception ex)
        {
            trace?.Invoke($"audit retention sweep failed (will retry next process start): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Replaces every character that can't appear in a file name -- path separators very much
    /// included -- with '_', and caps the result at 80 characters (PR review nit: an over-long
    /// hostile id should still get an audit entry rather than degrading to a MAX_PATH trace line;
    /// real ids are ~41 chars, and the timestamp keeps even a truncated name unique enough).
    /// Lossy on purpose: a mangled name for a malformed id beats an audit file escaping the
    /// workspace or not being written at all.
    /// </summary>
    private static string SanitizeForFileName(string executionId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(Math.Min(executionId.Length, 80));
        foreach (var ch in executionId)
        {
            if (builder.Length == 80)
            {
                break;
            }

            builder.Append(Array.IndexOf(invalid, ch) >= 0 || ch == '/' || ch == '\\' ? '_' : ch);
        }

        return builder.ToString();
    }

    private static void TryDelete(string file, DateTime cutoffUtc, Action<string>? trace)
    {
        try
        {
            if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
            {
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            trace?.Invoke($"audit retention sweep could not delete '{file}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string directory, DateTime cutoffUtc, Action<string>? trace)
    {
        try
        {
            // Age = the NEWEST of the directory's own stamp and its entries' write times. The
            // directory stamp ALONE is not enough (PR review corrected an NTFS misconception this
            // file's first draft recorded): a directory's LastWriteTime updates on child
            // create/delete/rename, NOT on content writes to an existing child -- so a scratch file
            // continuously rewritten in an old tmp dir would have read as aged and been deleted
            // mid-use. Checking the entries themselves makes "old" mean "nothing inside touched
            // within the retention window", which is the semantics the sweep actually wants.
            var newestUtc = Directory.GetLastWriteTimeUtc(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
            {
                var entryUtc = File.GetLastWriteTimeUtc(entry);
                if (entryUtc > newestUtc)
                {
                    newestUtc = entryUtc;
                }
            }

            if (newestUtc < cutoffUtc)
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            trace?.Invoke($"audit retention sweep could not delete '{directory}': {ex.GetType().Name}: {ex.Message}");
        }
    }
}
