using System;
using System.Collections.Generic;

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
    private readonly Dictionary<string, LinkedListNode<ExecutionRecord>> _byId = new();

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

    /// <summary>
    /// Appends a new record and, if that pushes the buffer past capacity, evicts the oldest entry.
    /// Returns false, and adds nothing, if <paramref name="record"/>'s ExecutionId already has an
    /// entry (a duplicate execution_id, which should never happen given the broker mints a fresh
    /// UUID-derived id per execution -- see ExecutionManager.Start's doc comment -- but a duplicate
    /// silently overwriting the _byId mapping while leaving the old node in _order would corrupt this
    /// buffer's core invariant: eviction removing the wrong entry's mapping, leaving a live execution
    /// permanently unreachable via TryGet even though its node is still sitting in _order. Fail loud
    /// at the boundary instead).
    ///
    /// Unlike <see cref="Prune"/>, capacity eviction here has no non-terminal exemption -- callers of this
    /// class don't need to provide one. A caller that also maintains the single-active-execution invariant
    /// this buffer was designed for (see ExecutionManager: it refuses to add a new record while the current
    /// one is still non-terminal) can never have that active record be the oldest-and-evicted entry -- it's
    /// always the most recently added, and nothing else can be appended behind it until it goes terminal
    /// itself.
    /// </summary>
    public bool Add(ExecutionRecord record)
    {
        lock (_lock)
        {
            if (_byId.ContainsKey(record.ExecutionId))
            {
                return false;
            }

            var node = _order.AddLast(record);
            _byId[record.ExecutionId] = node;

            while (_order.Count > _capacity)
            {
                var oldest = _order.First!;
                _order.RemoveFirst();
                _byId.Remove(oldest.Value.ExecutionId);
            }

            return true;
        }
    }

    public bool TryGet(string executionId, out ExecutionRecord? record)
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

    /// <summary>
    /// Removes terminal entries older than the retention window as of <paramref name="now"/>. Call
    /// periodically, not on every access.
    ///
    /// Second review finding: a still-active (non-terminal) record must never be evicted here, regardless
    /// of age -- age-based Prune() running long enough to age out the one execution that's actually still
    /// in flight would otherwise cause ExecutionManager.Transition()'s finishing-path
    /// methods (CompleteSuccess/CompleteError/etc.) to find no record for that execution_id when the script
    /// finally does finish, which without the ExecutionManager-side fix would throw from inside Revit's
    /// UI-thread Execute() callback -- exactly the crash class the terminal-race fix elsewhere in this
    /// class already eliminated, just via a different path. So a non-terminal record is skipped (left in
    /// place) rather than removed even once it's past the retention cutoff.
    /// </summary>
    public void Prune(DateTimeOffset now)
    {
        lock (_lock)
        {
            var cutoff = now - _retention;

            // _order is insertion-ordered oldest-first (the same invariant Add's own capacity eviction
            // relies on), so once a node's CreatedAt is newer than cutoff, every later node is too --
            // still safe to stop the whole scan there. Within the expired prefix, though, a non-terminal
            // node is skipped (not removed) and the scan continues past it to whatever expired node
            // follows, rather than assuming expired entries are removable as a block.
            var node = _order.First;
            while (node is not null && node.Value.CreatedAt <= cutoff)
            {
                var next = node.Next;
                if (node.Value.Status.IsTerminal())
                {
                    _order.Remove(node);
                    _byId.Remove(node.Value.ExecutionId);
                }

                node = next;
            }
        }
    }
}
