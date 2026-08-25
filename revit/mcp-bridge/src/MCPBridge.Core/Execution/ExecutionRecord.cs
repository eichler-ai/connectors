using System;
using System.Collections.Generic;
using MCPBridge.Core.Diagnostics;

namespace MCPBridge.Core.Execution;

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

    public object? Result { get; private set; }
    public string? StdOut { get; private set; }
    public DiagnosticRecord? Error { get; private set; }
    public IReadOnlyList<DiagnosticRecord> Notices { get; private set; } = Array.Empty<DiagnosticRecord>();

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

    public void MarkRunning(DateTimeOffset now)
    {
        RequireStatus(ExecutionStatus.Pending);
        Status = ExecutionStatus.Running;
        StartedAt = now;
    }

    public void MarkCompleted(DateTimeOffset now, object? result, string? stdOut, IReadOnlyList<DiagnosticRecord> notices)
    {
        RequireNonTerminal();
        Status = ExecutionStatus.Completed;
        CompletedAt = now;
        Result = result;
        StdOut = stdOut;
        Notices = notices;
    }

    public void MarkError(DateTimeOffset now, DiagnosticRecord error, string? stdOut)
    {
        RequireNonTerminal();
        Status = ExecutionStatus.Error;
        CompletedAt = now;
        Error = error;
        StdOut = stdOut;
    }

    public void MarkCancelled(DateTimeOffset now, string? stdOut)
    {
        RequireNonTerminal();
        Status = ExecutionStatus.Cancelled;
        CompletedAt = now;
        StdOut = stdOut;
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
