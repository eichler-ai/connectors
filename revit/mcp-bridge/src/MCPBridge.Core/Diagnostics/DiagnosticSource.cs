namespace MCPBridge.Core.Diagnostics;

/// <summary>
/// The add-in-side areas that can produce a diagnostic record. Each maps to a stable
/// "mcp-bridge.core.&lt;area&gt;" source tag (PRD §01) -- deliberately a closed set matching
/// the module layout, not a free-form string, so `source` can't drift into an
/// invented-per-feature taxonomy.
/// </summary>
public enum DiagnosticSource
{
    Execution,
    Connection,
    Discovery,
    Dialogs,

    /// <summary>
    /// The JSON-RPC envelope/param layer (<see cref="MCPBridge.Core.Protocol"/>) -- where a malformed
    /// request or a missing/wrong-typed param is detected, as distinct from the feature area that asked
    /// for the param. A caller told `mcp-bridge.core.protocol` should look at JsonRpcRequest's accessors,
    /// not at the discovery or execution code that called them.
    /// </summary>
    Protocol,
}

public static class DiagnosticSourceExtensions
{
    public static string ToSourceTag(this DiagnosticSource source) => source switch
    {
        DiagnosticSource.Execution => "mcp-bridge.core.execution",
        DiagnosticSource.Connection => "mcp-bridge.core.connection",
        DiagnosticSource.Discovery => "mcp-bridge.core.discovery",
        DiagnosticSource.Dialogs => "mcp-bridge.core.dialogs",
        DiagnosticSource.Protocol => "mcp-bridge.core.protocol",
        _ => throw new System.ArgumentOutOfRangeException(nameof(source), source, null),
    };
}
