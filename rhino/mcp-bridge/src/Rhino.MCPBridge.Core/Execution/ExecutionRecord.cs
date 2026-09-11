using System;
using System.Collections.Generic;
using Rhino.MCPBridge.Core.Diagnostics;

namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// Mutable record of one execute_script call's lifecycle (PRD §06). Lives in the
/// ExecutionRingBuffer independently of any socket, so poll_execution can resolve
/// it even after a broker restart.
/// </summary>
public sealed class ExecutionRecord
{
    /// <summary>
    /// Broker-minted, per PRD §01: "the add-in echoes the same ID back rather than
    /// generating its own." The Go broker mints IDs shaped "exec-&lt;uuid&gt;" (not
    /// Guid-parseable, due to the "exec-" prefix), so this is a plain string, not a Guid.
    /// </summary>
    public string ExecutionId { get; }
    public string ScriptText { get; }
    public long MaxDurationMs { get; }
    public DateTimeOffset CreatedAt { get; }

    public ExecutionStatus Status { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? CancellationRequestedAt { get; private set; }

    /// <summary>
    /// The completed run's return value, pre-formatted to its display string at completion time --
    /// deliberately a string and never the raw object, so nothing this ring-buffer-retained record
    /// holds can root a collectible script ALC or a Revit wrapper for the retention window (v1
    /// integrated review; see RequestDispatcher.SafeFormatReturnValue).
    /// </summary>
    public string? Result { get; private set; }
    public string? StdOut { get; private set; }
    public DiagnosticRecord? Error { get; private set; }
    public IReadOnlyList<DiagnosticRecord> Notices { get; private set; } = Array.Empty<DiagnosticRecord>();

    /// <summary>Files published via ScriptGlobals.Publish during this execution (PRD §09) -- a sibling list to Notices.</summary>
    public IReadOnlyList<PublishedFileRecord> Files { get; private set; } = Array.Empty<PublishedFileRecord>();

    /// <summary>#146 Phase 2: the completed run's net mutation report; null when nothing changed or the run did not complete.</summary>
    public MutationReport? Mutations { get; private set; }

    /// <summary>The run that completed on the same document before this one (PRD §05 observability);
    /// null when none had, or the run never resolved a document. Set once, by the dispatcher.</summary>
    public LastRun? PreviousRun { get; private set; }

    public void SetPreviousRun(LastRun? previous) => PreviousRun = previous;

    private ExecutionRecord(string executionId, string scriptText, long maxDurationMs, DateTimeOffset createdAt)
    {
        ExecutionId = executionId;
        ScriptText = scriptText;
        MaxDurationMs = maxDurationMs;
        CreatedAt = createdAt;
        Status = ExecutionStatus.Pending;
    }

    public static ExecutionRecord CreatePending(string executionId, string scriptText, long maxDurationMs, DateTimeOffset createdAt) =>
        new(executionId, scriptText, maxDurationMs, createdAt);

    /// <summary>Running -&gt; Pending: the run's command could not start because Rhino was inside another
    /// command (rhino/docs/PRD.md §06); the launcher retries and the agent keeps seeing `pending`.</summary>
    public void MarkPendingAgain()
    {
        if (Status == ExecutionStatus.Running)
        {
            Status = ExecutionStatus.Pending;
            StartedAt = null;
        }
    }

    public void MarkRunning(DateTimeOffset now)
    {
        RequireStatus(ExecutionStatus.Pending);
        Status = ExecutionStatus.Running;
        StartedAt = now;
    }

    public void MarkCompleted(DateTimeOffset now, string? result, string? stdOut, IReadOnlyList<DiagnosticRecord> notices, IReadOnlyList<PublishedFileRecord>? files = null, MutationReport? mutations = null)
    {
        RequireNonTerminal();
        Status = ExecutionStatus.Completed;
        CompletedAt = now;
        Result = result;
        StdOut = stdOut;
        Notices = notices;
        Files = files ?? Array.Empty<PublishedFileRecord>();
        Mutations = mutations;
    }

    public void MarkError(DateTimeOffset now, DiagnosticRecord error, string? stdOut, IReadOnlyList<DiagnosticRecord>? notices = null, IReadOnlyList<PublishedFileRecord>? files = null)
    {
        RequireNonTerminal();
        Status = ExecutionStatus.Error;
        CompletedAt = now;
        Error = error;
        StdOut = stdOut;
        Notices = notices ?? Array.Empty<DiagnosticRecord>();
        Files = files ?? Array.Empty<PublishedFileRecord>();
    }

    public void MarkCancelled(DateTimeOffset now, string? stdOut, IReadOnlyList<DiagnosticRecord>? notices = null, IReadOnlyList<PublishedFileRecord>? files = null)
    {
        RequireNonTerminal();
        Status = ExecutionStatus.Cancelled;
        CompletedAt = now;
        StdOut = stdOut;
        Notices = notices ?? Array.Empty<DiagnosticRecord>();
        Files = files ?? Array.Empty<PublishedFileRecord>();
    }

    public void MarkUnrecoverable(DateTimeOffset now, DiagnosticRecord diagnostic)
    {
        RequireNonTerminal();
        Status = ExecutionStatus.Unrecoverable;
        CompletedAt = now;
        Error = diagnostic;
    }

    public void RequestCancellation(DateTimeOffset now)
    {
        RequireNonTerminal();
        CancellationRequestedAt ??= now;
    }

    private void RequireStatus(ExecutionStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException(
                $"Execution {ExecutionId} is in state {Status}, expected {expected}.");
        }
    }

    private void RequireNonTerminal()
    {
        if (Status.IsTerminal())
        {
            throw new InvalidOperationException(
                $"Execution {ExecutionId} is already terminal ({Status}) and cannot transition further.");
        }
    }
}
