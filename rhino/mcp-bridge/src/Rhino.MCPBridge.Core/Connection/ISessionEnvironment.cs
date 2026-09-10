using Rhino.MCPBridge.Core.Protocol;

namespace Rhino.MCPBridge.Core.Connection;

/// <summary>
/// What a <see cref="ConnectionSession"/> needs from its host, kept to an interface so the session's
/// whole state machine is tier-1 testable over an in-memory stream with no Rhino and no socket.
/// </summary>
internal interface ISessionEnvironment
{
    /// <summary>This instance's auth token, from the instance file.</summary>
    string Token { get; }

    /// <summary>The current snapshot for `register`. MUST NOT block on the main thread: the host keeps a
    /// cache it rebuilds on the main thread at load and on every document event, so a Rhino whose main
    /// thread is inside a modal (the template chooser on a fresh launch) still registers, with the
    /// documents it had last reported (review of #281).</summary>
    RegisterSnapshot Snapshot();

    /// <summary>Answers one request after auth. Returns the complete JSON-RPC response line (result or error).</summary>
    Task<string> DispatchAsync(JsonRpcRequest request, CancellationToken cancellationToken);

    /// <summary>Best-effort diagnostic sink (the connection log).</summary>
    void Log(string message);
}
