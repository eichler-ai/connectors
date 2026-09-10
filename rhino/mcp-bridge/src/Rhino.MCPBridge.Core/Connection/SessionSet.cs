namespace Rhino.MCPBridge.Core.Connection;

/// <summary>The live, authenticated sessions, so the host can push one line to all of them (a
/// register refresh on a document event, the heartbeat ping). Bounded by construction: a session is
/// removed the moment its RunAsync returns.</summary>
internal sealed class SessionSet
{
    private readonly object _lock = new();
    private readonly List<ConnectionSession> _sessions = new();

    public int Count { get { lock (_lock) { return _sessions.Count; } } }

    public IReadOnlyList<ConnectionSession> Snapshot() { lock (_lock) { return _sessions.ToArray(); } }

    public void Add(ConnectionSession s) { lock (_lock) { _sessions.Add(s); } }

    public void Remove(ConnectionSession s) { lock (_lock) { _sessions.Remove(s); } }

    /// <summary>Best-effort fan-out; a session whose write fails is left for its own RunAsync to tear down.</summary>
    public async Task BroadcastAsync(string json, CancellationToken cancellationToken)
    {
        foreach (var s in Snapshot())
        {
            if (!s.Authenticated) continue;
            try { await s.SendAsync(json, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { /* the session's own loop reports and removes it */ }
        }
    }
}
