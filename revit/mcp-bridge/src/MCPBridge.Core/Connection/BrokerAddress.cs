namespace MCPBridge.Core.Connection;

/// <summary>The host:port a discovered broker is reachable at, parsed from broker.json's own
/// host/port fields. (This type once also carried the removed remote-mode fallback address --
/// see BrokerDiscoveryOptions for why that config surface is gone.)</summary>
public sealed class BrokerAddress
{
    public string Host { get; }
    public int Port { get; }

    public BrokerAddress(string host, int port)
    {
        Host = host;
        Port = port;
    }
}
