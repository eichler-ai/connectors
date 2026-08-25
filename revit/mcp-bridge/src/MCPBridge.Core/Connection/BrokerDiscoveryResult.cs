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
    /// writes a real bound address, never 0.0.0.0), and on the not-found path from the
    /// configured remote-mode fallback (see <see cref="Fallback"/>) when one applies --
    /// so a caller always has a single, unambiguous place to get a usable address from
    /// regardless of which path produced this result.
    /// </summary>
    public BrokerAddress? Address { get; }

    /// <summary>
    /// Remote-mode-only: the configured fallback host:port, when the shared drive/broker.json
    /// wasn't reachable and one applies. Always exactly <see cref="Address"/> on the not-found path
    /// (there is no other source of an address once discovery has failed) and always null on a
    /// successful discovery -- so it's derived from <see cref="Found"/>/<see cref="Address"/> rather
    /// than tracked as separate state.
    /// </summary>
    public BrokerAddress? Fallback => Found ? null : Address;

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

    public static BrokerDiscoveryResult NotFound(BrokerAddress? fallback = null, DiagnosticRecord? diagnostic = null) =>
        new(false, null, fallback, diagnostic);
}
