using System;
using System.Collections.Generic;
using System.Threading;

namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// One monotonic clock over the events the undo tool's gate reasons about (PRD §07): document
/// changes made OUTSIDE the connector's own work ("foreign": a person, another plug-in), and the
/// connector's own completed runs and undo/redo operations, each stamped with a tick. The adapter
/// reports every document change on the main thread; changes to the document the connector is
/// working on while it is inside its own command (<see cref="EnterConnectorWork"/>) are the
/// connector's and are not foreign -- changes to any OTHER document in that window still are.
/// Why not Command.LastCommandId: a read-only run of ours also leaves the run command as Rhino's
/// last command, so "last command was ours" says nothing about whose entry is on top of the undo
/// stack (verified live in the phase 1 PR 5 harness).
/// </summary>
internal sealed class ChangeClock
{
    private long _tick;
    private readonly object _lock = new();
    private readonly Dictionary<string, long> _lastForeignChange = new();
    private readonly Dictionary<string, int> _insideWork = new();

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

    /// <summary>Called by the adapter on every document change; ignored for a document the connector is inside its own work on.</summary>
    public void NoteChange(string documentId)
    {
        lock (_lock)
        {
            if (_insideWork.TryGetValue(documentId, out var n) && n > 0)
            {
                return;
            }

            _lastForeignChange[documentId] = Next();
        }
    }

    /// <summary>Drops what is known about a document (closed); a reopened saved document starts clean.</summary>
    public void Forget(string documentId)
    {
        lock (_lock)
        {
            _lastForeignChange.Remove(documentId);
        }
    }

    /// <summary>Marks the connector's own work on <paramref name="documentId"/> (a run, an undo/redo) so
    /// its changes there are not foreign. Nestable.</summary>
    public IDisposable EnterConnectorWork(string documentId)
    {
        lock (_lock)
        {
            _insideWork[documentId] = (_insideWork.TryGetValue(documentId, out var n) ? n : 0) + 1;
        }

        return new Exit(this, documentId);
    }

    private sealed class Exit : IDisposable
    {
        private ChangeClock? _c;
        private readonly string _doc;
        public Exit(ChangeClock c, string doc) { _c = c; _doc = doc; }
        public void Dispose()
        {
            var c = Interlocked.Exchange(ref _c, null);
            if (c is null) return;
            lock (c._lock)
            {
                if (c._insideWork.TryGetValue(_doc, out var n) && n > 1) c._insideWork[_doc] = n - 1;
                else c._insideWork.Remove(_doc);
            }
        }
    }
}
