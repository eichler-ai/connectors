using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCPBridge.Core.Protocol;

/// <summary>
/// The `ping` notification the add-in sends periodically over an already-registered
/// connection (PRD §05 "Heartbeat, not just connection state"), so the broker can tell a
/// live-but-wedged Revit process apart from a merely-quiet one. Deliberately minimal --
/// no params, nothing for the broker to act on beyond recording that this connection is
/// still alive; the broker's RecordPing just needs the notification to have arrived on a
/// specific instance's connection, not any payload inside it.
/// </summary>
public static class PingMessage
{
    private sealed class Envelope
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("method")]
        public string Method { get; set; } = "ping";
    }

    public static string ToJson() => JsonSerializer.Serialize(new Envelope(), WireJson.Compact);
}
