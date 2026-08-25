using System.Text.Json;

namespace MCPBridge.Core.Protocol;

/// <summary>
/// Shared serializer options for every outbound wire message (<see cref="AuthMessage"/>,
/// <see cref="RegisterMessage"/>, and any future one) -- factored out so "compact,
/// single-line output" (NDJSON framing requires no embedded newlines) is defined once
/// rather than redeclared per message type.
/// </summary>
internal static class WireJson
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
    };
}
