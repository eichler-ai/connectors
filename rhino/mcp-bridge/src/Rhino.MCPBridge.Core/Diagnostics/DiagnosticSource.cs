namespace Rhino.MCPBridge.Core.Diagnostics;

/// <summary>
/// The `source` of a diagnostic record (PRD §01 via Revit §01): a module name matching the repo
/// layout, never a per-feature invention. The tags are the same shape as the Revit connector's so
/// an agent that learned one connector's records reads the other's.
/// </summary>
public enum DiagnosticSource
{
    Execution,
    Connection,
    Discovery,
    Dialogs,
    Protocol,
    Grasshopper,
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
        DiagnosticSource.Grasshopper => "mcp-bridge.core.grasshopper",
        _ => throw new System.ArgumentOutOfRangeException(nameof(source), source, null),
    };
}
