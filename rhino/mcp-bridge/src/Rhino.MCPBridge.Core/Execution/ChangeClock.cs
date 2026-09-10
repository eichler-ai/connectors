using System;
using System.Collections.Generic;
using System.Threading;

namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// One monotonic clock over the events the undo tool's gate reasons about (PRD §07): document
/// changes made OUTSIDE the connector's own work ("foreign": a person, another plug-in), and the
/// connector's own completed runs and undo/redo operations, each stamped with a tick. The adapter
/// reports every document change on the main thread; changes made while the connector is inside
/// its own command (<see cref="EnterConnectorWork"/>) are the connector's and are not foreign.
/// Why not Command.LastCommandId: a read-only run of ours also leaves the run command as Rhino's
/// last command, so "last command was ours" says nothing about whose entry is on top of the undo
/// stack (verified live in the phase 1 PR 5 harness).
/// </summary>
internal sealed class ChangeClock
{
    private long _tick;
    private int _insideConnectorWork;
    private readonly object _lock = new();
    private readonly Dictionary<string, long> _lastForeignChange = new();

    /// <summary>Takes the next tick; every stamped event uses one.</summary>
    public long Next() => Interlocked.Increment(ref _tick);

    /// <summary>The tick of the last foreign change on the document, 0 when none was seen.</summary>
    public long LastForeignChange(string documentId)
    {
        lock (_lock)
        {
            return _lastForeignChange.TryGetValue(documentId, out var t) ? t : 0;
        }
    }

    /// <summary>Called by the adapter on every document change; ignored while the connector is inside its own work.</summary>
    public void NoteChange(string documentId)
    {
        if (Volatile.Read(ref _insideConnectorWork) > 0)
        {
            return;
        }

        var t = Next();
        lock (_lock)
        {
            _lastForeignChange[documentId] = t;
        }
    }

    /// <summary>Marks the connector's own work (a run, an undo/redo) so its changes are not foreign. Main thread; nestable.</summary>
    public IDisposable EnterConnectorWork()
    {
        Interlocked.Increment(ref _insideConnectorWork);
        return new Exit(this);
    }

    private sealed class Exit : IDisposable
    {
        private ChangeClock? _c;
        public Exit(ChangeClock c) { _c = c; }
        public void Dispose() { var c = Interlocked.Exchange(ref _c, null); if (c is not null) Interlocked.Decrement(ref c._insideConnectorWork); }
    }
}
