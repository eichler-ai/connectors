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
        catch (Exception ex)
        {
            // The broker may be mid-write; treat as a transient not-found rather than a hard error.
            // Exception, not just IOException (v1 integrated review): a read of broker.json can also
            // throw UnauthorizedAccessException (an ACL hiccup, or a remote-mode UNC share flapping
            // mid-read) among others, and any escape from here reaches the connection thread's loop --
            // where an unhandled exception doesn't just end this attempt, it can end the loop (or the
            // process). Every read failure here means the same thing operationally: no usable
            // broker.json this attempt; retry on the next one.
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

    private static BrokerDiscoveryResult BuildNotFound(DiagnosticRecord? diagnostic = null)
    {
        // No fallback address on the not-found path any more -- see BrokerDiscoveryOptions for why
        // the remote-mode fallback host:port was removed (an address with no token can never
        // authenticate, so it was dead configuration).
        return BrokerDiscoveryResult.NotFound(diagnostic);
    }
}
