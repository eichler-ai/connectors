using System;
using System.Collections.Generic;

namespace MCPBridge.Core.Execution;

/// <summary>
/// A small bounded LRU cache (PRD §06: "a small bounded LRU, e.g. last 20-50 unique
/// scripts"). Generic and independent of Roslyn so it can be tested in isolation;
/// ScriptCompilationCache is the Roslyn-specific wrapper around this.
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly LinkedList<(TKey Key, TValue Value)> _order = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = new();
    private readonly object _lock = new();

    public LruCache(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be positive.");
        }

        _capacity = capacity;
    }

    public int Count
    {
        get { lock (_lock) { return _order.Count; } }
    }

    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }

            value = default;
            return false;
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
            }

            var node = _order.AddFirst((key, value));
            _map[key] = node;

            while (_order.Count > _capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }
}
