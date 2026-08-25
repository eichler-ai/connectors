namespace MCPBridge.Core.Connection;

/// <summary>An explicit host:port fallback used when remote-mode discovery finds no shared drive (PRD §05).</summary>
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
