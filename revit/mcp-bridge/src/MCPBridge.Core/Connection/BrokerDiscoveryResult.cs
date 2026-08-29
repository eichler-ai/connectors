using MCPBridge.Core.Diagnostics;

namespace MCPBridge.Core.Connection;

/// <summary>Outcome of one broker.json discovery attempt (PRD §05: the add-in re-reads this on every retry).</summary>
public sealed class BrokerDiscoveryResult
{
    public bool Found { get; }
    public BrokerJson? BrokerJson { get; }

    /// <summary>
    /// The host:port a caller should actually connect a TCP client to. Populated on a
    /// successful discovery from the parsed broker.json's Host/Port (the broker always
    /// writes a real bound address, never 0.0.0.0); always null on the not-found path.
    /// (A remote-mode fallback address used to populate this on not-found too -- removed,
    /// see <see cref="BrokerDiscoveryOptions"/>: with auth mandatory and the token only
    /// ever in broker.json, an address without a broker.json was never connectable.)
    /// </summary>
    public BrokerAddress? Address { get; }

    /// <summary>Set when broker.json exists but failed to parse -- a real condition worth surfacing, not a plain not-found.</summary>
    public DiagnosticRecord? Diagnostic { get; }

    private BrokerDiscoveryResult(bool found, BrokerJson? brokerJson, BrokerAddress? address, DiagnosticRecord? diagnostic)
    {
        Found = found;
        BrokerJson = brokerJson;
        Address = address;
        Diagnostic = diagnostic;
    }

    public static BrokerDiscoveryResult FoundResult(BrokerJson brokerJson) =>
        new(true, brokerJson, new BrokerAddress(brokerJson.Host, brokerJson.Port), null);

    public static BrokerDiscoveryResult NotFound(DiagnosticRecord? diagnostic = null) =>
        new(false, null, null, diagnostic);
}
