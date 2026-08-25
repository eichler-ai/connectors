using System;
using MCPBridge.Core.Diagnostics;
using MCPBridge.Core.Execution;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

public class ExecutionRingBufferTests
{
    private static ExecutionRecord NewRecord(DateTimeOffset createdAt) =>
        ExecutionRecord.CreatePending(Guid.NewGuid(), "// script", maxDurationMs: 600_000, createdAt: createdAt);

    // Prune() (second review, Fix 5) only ever evicts terminal records -- age-based pruning tests that mean
    // to exercise actual eviction need a record that has already resolved, not a still-Pending one.
    private static ExecutionRecord NewTerminalRecord(DateTimeOffset createdAt)
    {
        var record = NewRecord(createdAt);
        record.MarkCompleted(createdAt, result: null, stdOut: null, notices: Array.Empty<DiagnosticRecord>());
        return record;
    }

    [Fact]
    public void Add_ThenTryGet_ReturnsSameRecord()
    {
        var buffer = new ExecutionRingBuffer(capacity: 10, retention: TimeSpan.FromMinutes(10));
        var record = NewRecord(DateTimeOffset.UtcNow);

        buffer.Add(record);

        Assert.True(buffer.TryGet(record.ExecutionId, out var found));
        Assert.Same(record, found);
    }

    [Fact]
    public void TryGet_UnknownId_ReturnsFalse()
    {
        var buffer = new ExecutionRingBuffer(capacity: 10, retention: TimeSpan.FromMinutes(10));

        Assert.False(buffer.TryGet(Guid.NewGuid(), out var found));
        Assert.Null(found);
    }

    [Fact]
    public void SurvivesReconnect_RecordStillResolvesAfterAdd()
    {
        // The ring buffer is deliberately independent of any socket/connection object --
        // this is what lets poll_execution keep resolving after a broker restart (PRD §05).
        var buffer = new ExecutionRingBuffer(capacity: 10, retention: TimeSpan.FromMinutes(10));
        var record = NewRecord(DateTimeOffset.UtcNow);
        buffer.Add(record);

        // Simulate "broker restarted, add-in reconnected" -- buffer wasn't touched by that at all.
        Assert.True(buffer.TryGet(record.ExecutionId, out _));
    }

    [Fact]
    public void CapacityExceeded_EvictsOldestFirst()
    {
        var buffer = new ExecutionRingBuffer(capacity: 2, retention: TimeSpan.FromMinutes(10));
        var now = DateTimeOffset.UtcNow;
        var first = NewRecord(now);
        var second = NewRecord(now.AddSeconds(1));
        var third = NewRecord(now.AddSeconds(2));

        buffer.Add(first);
        buffer.Add(second);
        buffer.Add(third);

        Assert.False(buffer.TryGet(first.ExecutionId, out _));
        Assert.True(buffer.TryGet(second.ExecutionId, out _));
        Assert.True(buffer.TryGet(third.ExecutionId, out _));
    }

    [Fact]
    public void PruneOlderThanRetention_RemovesAgedOutEntries()
    {
        var buffer = new ExecutionRingBuffer(capacity: 100, retention: TimeSpan.FromMinutes(10));
        var now = DateTimeOffset.UtcNow;
        var old = NewTerminalRecord(now.AddMinutes(-11));
        var recent = NewTerminalRecord(now.AddMinutes(-1));

        buffer.Add(old);
        buffer.Add(recent);

        buffer.Prune(now);

        Assert.False(buffer.TryGet(old.ExecutionId, out _));
        Assert.True(buffer.TryGet(recent.ExecutionId, out _));
    }

    [Fact]
    public void PruneExactlyAtRetentionBoundary_IsRemoved()
    {
        var buffer = new ExecutionRingBuffer(capacity: 100, retention: TimeSpan.FromMinutes(10));
        var now = DateTimeOffset.UtcNow;
        var boundary = NewTerminalRecord(now.AddMinutes(-10));

        buffer.Add(boundary);
        buffer.Prune(now);

        Assert.False(buffer.TryGet(boundary.ExecutionId, out _));
    }

    // --- Second review, Fix 5: a still-active (non-terminal) record must never be evicted by Prune() ---

    [Fact]
    public void Prune_NeverEvictsANonTerminalRecord_EvenPastRetention()
    {
        var buffer = new ExecutionRingBuffer(capacity: 100, retention: TimeSpan.FromMinutes(10));
        var now = DateTimeOffset.UtcNow;
        var stillActive = NewRecord(now.AddMinutes(-11)); // Pending -- CreatePending never marks terminal
        Assert.False(stillActive.Status.IsTerminal());

        buffer.Add(stillActive);
        buffer.Prune(now);

        Assert.True(buffer.TryGet(stillActive.ExecutionId, out var found));
        Assert.Same(stillActive, found);
    }

    [Fact]
    public void Prune_EvictsAgedOutTerminalRecords_ButSkipsAndKeepsScanningPastANonTerminalOneAheadOfThem()
    {
        // A non-terminal record blocking the front of the insertion-ordered list must not stop the scan
        // from still evicting aged-out terminal records behind it.
        var buffer = new ExecutionRingBuffer(capacity: 100, retention: TimeSpan.FromMinutes(10));
        var now = DateTimeOffset.UtcNow;
        var stillActive = NewRecord(now.AddMinutes(-15));
        var agedTerminal = NewRecord(now.AddMinutes(-14));
        agedTerminal.MarkCompleted(now.AddMinutes(-14), result: null, stdOut: null, notices: Array.Empty<DiagnosticRecord>());
        var recent = NewRecord(now.AddMinutes(-1));

        buffer.Add(stillActive);
        buffer.Add(agedTerminal);
        buffer.Add(recent);

        buffer.Prune(now);

        Assert.True(buffer.TryGet(stillActive.ExecutionId, out _), "non-terminal record must survive Prune regardless of age");
        Assert.False(buffer.TryGet(agedTerminal.ExecutionId, out _), "aged-out terminal record behind a non-terminal one must still be pruned");
        Assert.True(buffer.TryGet(recent.ExecutionId, out _));
    }
}
