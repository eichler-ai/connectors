using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCPBridge.Core.Protocol;

/// <summary>
/// The `ping` notification the add-in sends periodically over an already-registered
/// connection (PRD §05 "Heartbeat, not just connection state"), so the broker can tell a
/// live-but-wedged Revit process apart from a merely-quiet one. The bare form carries no
/// params -- the broker's RecordPing needs only that the notification arrived on a specific
/// instance's connection. Since issue #31 it may OPTIONALLY carry a <see cref="MemorySnapshot"/>
/// (params.memory), read on the heartbeat's own background thread so it keeps flowing even while
/// a script runs or the UI thread is wedged; older brokers ignore the extra field.
/// </summary>
public static class PingMessage
{
    private sealed class ParamsDto
    {
        [JsonPropertyName("memory")]
        public MemorySnapshot? Memory { get; set; }
    }

    private sealed class Envelope
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("method")]
        public string Method { get; set; } = "ping";

        // Omitted entirely when null so the bare heartbeat stays exactly as it was (no "params" key).
        [JsonPropertyName("params")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ParamsDto? Params { get; set; }
    }

    /// <summary>A bare heartbeat with no params (PRD §05).</summary>
    public static string ToJson() => JsonSerializer.Serialize(new Envelope(), WireJson.Compact);

    /// <summary>A heartbeat carrying a memory sample (issue #31).</summary>
    public static string ToJson(MemorySnapshot memory) =>
        JsonSerializer.Serialize(new Envelope { Params = new ParamsDto { Memory = memory } }, WireJson.Compact);
}
