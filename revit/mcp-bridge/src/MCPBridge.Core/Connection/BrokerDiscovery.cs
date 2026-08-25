using System.Collections.Generic;
using System.IO;
using MCPBridge.Core.Diagnostics;

namespace MCPBridge.Core.Connection;

/// <summary>
/// Reads broker.json to find the broker's port/token, per PRD §05. Called on every
/// retry of the reconnect loop -- not just once at startup -- since the broker may
/// not have started yet, or may have restarted with a new port/token.
/// </summary>
public sealed class BrokerDiscovery
{
    private readonly BrokerDiscoveryOptions _options;

    public BrokerDiscovery(BrokerDiscoveryOptions options)
    {
        _options = options;
    }

    public string BrokerJsonPath => Path.Combine(_options.ConnectorRoot, "broker.json");

    public BrokerDiscoveryResult TryDiscover()
    {
        if (!File.Exists(BrokerJsonPath))
        {
            return BuildNotFound();
        }

        string text;
        try
        {
            text = File.ReadAllText(BrokerJsonPath);
        }
        catch (IOException ex)
        {
            // The broker may be mid-write; treat as a transient not-found rather than a hard error.
            return BuildNotFound(DiagnosticRecord.Create(
                DiagnosticSeverity.Warning,
                "broker-json-unreadable",
                DiagnosticSource.Connection,
                $"broker.json at '{BrokerJsonPath}' could not be read: {ex.Message}",
                detail: new Dictionary<string, object?> { ["path"] = BrokerJsonPath },
                remedy: new[] { "Retry on the next reconnect attempt." }));
        }

        try
        {
            var brokerJson = BrokerJson.Parse(text);
            return BrokerDiscoveryResult.FoundResult(brokerJson);
        }
        catch (BrokerJsonParseException ex)
        {
            return BuildNotFound(DiagnosticRecord.Create(
                DiagnosticSeverity.Warning,
                "broker-json-invalid",
                DiagnosticSource.Connection,
                $"broker.json at '{BrokerJsonPath}' failed to parse: {ex.Message}",
                detail: new Dictionary<string, object?> { ["path"] = BrokerJsonPath },
                remedy: new[] { "Wait for the broker to finish writing broker.json and retry." }));
        }
    }

    private BrokerDiscoveryResult BuildNotFound(DiagnosticRecord? diagnostic = null)
    {
        BrokerAddress? fallback = null;
        if (_options.Mode == BrokerTopologyMode.Remote &&
            _options.FallbackHost is not null &&
            _options.FallbackPort is not null)
        {
            fallback = new BrokerAddress(_options.FallbackHost, _options.FallbackPort.Value);
        }

        return BrokerDiscoveryResult.NotFound(fallback, diagnostic);
    }
}
