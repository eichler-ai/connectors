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

    /// <summary>Best-effort fan-out, in parallel: one server that has stopped reading must not delay
    /// the ping every other server is waiting for (review of #281). A send that fails or times out is
    /// torn down by <see cref="ConnectionSession.SendAsync"/> itself.</summary>
    public Task BroadcastAsync(string json, CancellationToken cancellationToken)
    {
        var sends = new List<Task>();
        foreach (var s in Snapshot())
        {
            if (!s.Authenticated) continue;
            sends.Add(SendOne(s, json, cancellationToken));
        }

        return Task.WhenAll(sends);
    }

    private static async Task SendOne(ConnectionSession s, string json, CancellationToken cancellationToken)
    {
        try { await s.SendAsync(json, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { /* the session's own loop reports and removes it */ }
    }
}
