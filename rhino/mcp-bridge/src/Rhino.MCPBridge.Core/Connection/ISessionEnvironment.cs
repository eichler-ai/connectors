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

    /// <summary>A fresh snapshot for `register`; the host is responsible for taking it on the main thread.</summary>
    RegisterSnapshot Snapshot();

    /// <summary>Answers one request after auth. Returns the complete JSON-RPC response line (result or error).</summary>
    Task<string> DispatchAsync(JsonRpcRequest request, CancellationToken cancellationToken);

    /// <summary>Best-effort diagnostic sink (the connection log).</summary>
    void Log(string message);
}
