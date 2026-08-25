using System;
using System.Collections.Generic;
using System.Linq;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Ring buffer of recent execution results (PRD §05: "last N / ~10 minutes"), kept
/// independently of the TCP socket so poll_execution against an execution_id still
/// resolves after a broker restart, as long as Revit itself didn't also restart.
/// Bounded by both a max entry count and an age-based retention window; whichever
/// evicts first wins.
/// </summary>
public sealed class ExecutionRingBuffer
{
    private readonly int _capacity;
    private readonly TimeSpan _retention;
    private readonly object _lock = new();

    // Insertion-ordered so capacity eviction removes the oldest entry first.
    private readonly LinkedList<ExecutionRecord> _order = new();
    private readonly Dictionary<Guid, LinkedListNode<ExecutionRecord>> _byId = new();

    public ExecutionRingBuffer(int capacity, TimeSpan retention)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be positive.");
        }

        _capacity = capacity;
        _retention = retention;
    }

    /// <summary>Default per PRD §05: ~50 entries or 10 minutes, whichever comes first.</summary>
    public static ExecutionRingBuffer CreateDefault() => new(capacity: 50, retention: TimeSpan.FromMinutes(10));

    public void Add(ExecutionRecord record)
    {
        lock (_lock)
        {
            var node = _order.AddLast(record);
            _byId[record.ExecutionId] = node;

            while (_order.Count > _capacity)
            {
                var oldest = _order.First!;
                _order.RemoveFirst();
                _byId.Remove(oldest.Value.ExecutionId);
            }
        }
    }

    public bool TryGet(Guid executionId, out ExecutionRecord? record)
    {
        lock (_lock)
        {
            if (_byId.TryGetValue(executionId, out var node))
            {
                record = node.Value;
                return true;
            }

            record = null;
            return false;
        }
    }

    /// <summary>Removes entries older than the retention window as of <paramref name="now"/>. Call periodically, not on every access.</summary>
    public void Prune(DateTimeOffset now)
    {
        lock (_lock)
        {
            var cutoff = now - _retention;
            var toRemove = _order.Where(r => r.CreatedAt <= cutoff).ToList();
            foreach (var record in toRemove)
            {
                _byId.Remove(record.ExecutionId);
                _order.Remove(record);
            }
        }
    }
}
