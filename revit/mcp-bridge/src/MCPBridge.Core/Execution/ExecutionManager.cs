using System;
using System.Collections.Generic;
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
            return ExecuteOutcome.Started(record);
        }
    }

    public void MarkRunning(Guid executionId, DateTimeOffset now)
    {
        RequireRecord(executionId).MarkRunning(now);
    }

    public void CompleteSuccess(Guid executionId, DateTimeOffset now, object? result, string? stdOut, IReadOnlyList<DiagnosticRecord> notices)
    {
        RequireRecord(executionId).MarkCompleted(now, result, stdOut, notices);
        ClearActiveIfMatches(executionId);
    }

    public void CompleteError(Guid executionId, DateTimeOffset now, DiagnosticRecord error, string? stdOut)
    {
        RequireRecord(executionId).MarkError(now, error, stdOut);
        ClearActiveIfMatches(executionId);
    }

    public void CompleteCancelled(Guid executionId, DateTimeOffset now, string? stdOut)
    {
        RequireRecord(executionId).MarkCancelled(now, stdOut);
        ClearActiveIfMatches(executionId);
    }

    public CancellationRequestOutcome RequestCancellation(Guid executionId, DateTimeOffset now)
    {
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

            record.RequestCancellation(now);
            return CancellationRequestOutcome.Acknowledged;
        }
    }

    /// <summary>Auto-cancels the active execution once max_duration_ms has elapsed since it started running (PRD §06). Idempotent.</summary>
    public void CheckMaxDuration(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_active is not { Status: ExecutionStatus.Running, StartedAt: { } startedAt } record)
            {
                return;
            }

            if (record.CancellationRequestedAt is not null)
            {
                return;
            }

            var elapsedMs = (now - startedAt).TotalMilliseconds;
            if (elapsedMs >= record.MaxDurationMs)
            {
                record.RequestCancellation(now);
            }
        }
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
        }
    }

    public ExecutionRecord? Poll(Guid executionId)
    {
        _ringBuffer.TryGet(executionId, out var record);
        return record;
    }

    private ExecutionRecord RequireRecord(Guid executionId)
    {
        if (!_ringBuffer.TryGet(executionId, out var record) || record is null)
        {
            throw new InvalidOperationException($"Unknown execution_id {executionId}.");
        }

        return record;
    }

    private void ClearActiveIfMatches(Guid executionId)
    {
        lock (_lock)
        {
            if (_active?.ExecutionId == executionId)
            {
                _active = null;
            }
        }
    }

    private static DiagnosticRecord UnrecoverableDiagnostic() => DiagnosticRecord.Create(
        DiagnosticSeverity.Error,
        "instance-unrecoverable",
        DiagnosticSource.Execution,
        "this Revit instance is unrecoverable after a cancellation that did not complete within its grace period.",
        detail: null,
        remedy: new[] { "Restart Revit to recover this instance; a fresh instance_id will be issued on reconnect." });
}
