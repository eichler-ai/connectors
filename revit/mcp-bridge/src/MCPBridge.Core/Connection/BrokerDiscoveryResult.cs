using MCPBridge.Core.Diagnostics;

namespace MCPBridge.Core.Connection;

/// <summary>Outcome of one broker.json discovery attempt (PRD §05: the add-in re-reads this on every retry).</summary>
public sealed class BrokerDiscoveryResult
{
    public bool Found { get; }
    public BrokerJson? BrokerJson { get; }

    /// <summary>Remote-mode-only: set when the shared drive/broker.json wasn't reachable but a configured fallback host:port exists.</summary>
    public BrokerAddress? Fallback { get; }

    /// <summary>Set when broker.json exists but failed to parse -- a real condition worth surfacing, not a plain not-found.</summary>
    public DiagnosticRecord? Diagnostic { get; }

    private BrokerDiscoveryResult(bool found, BrokerJson? brokerJson, BrokerAddress? fallback, DiagnosticRecord? diagnostic)
    {
        Found = found;
        BrokerJson = brokerJson;
        Fallback = fallback;
        Diagnostic = diagnostic;
    }

    public static BrokerDiscoveryResult FoundResult(BrokerJson brokerJson) => new(true, brokerJson, null, null);

    public static BrokerDiscoveryResult NotFound(BrokerAddress? fallback = null, DiagnosticRecord? diagnostic = null) =>
        new(false, null, fallback, diagnostic);
}
