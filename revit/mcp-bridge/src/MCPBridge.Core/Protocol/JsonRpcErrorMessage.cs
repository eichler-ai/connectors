using System.Text.Json;
using System.Text.Json.Serialization;
using MCPBridge.Core.Diagnostics;

namespace MCPBridge.Core.Protocol;

/// <summary>Standard JSON-RPC 2.0 error codes, mirroring the Go broker's own constants (transport/rpc.go) so both sides speak the same numeric vocabulary.</summary>
public static class JsonRpcErrorCode
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

/// <summary>
/// Serializes a JSON-RPC 2.0 error response: {"jsonrpc":"2.0","id":&lt;echoed&gt;,"error":{"code",
/// "message","data"}} -- matching transport.NewErrorResponse on the Go side exactly, including using the
/// shared diagnostic-record shape (<see cref="DiagnosticRecord"/>) as the error's `data` field rather than
/// a bare string (PRD §01).
/// </summary>
public static class JsonRpcErrorMessage
{
    private sealed class ErrorDto
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("data")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DiagnosticRecord? Data { get; set; }
    }

    private sealed class Envelope
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public JsonElement Id { get; set; }

        [JsonPropertyName("error")]
        public ErrorDto Error { get; set; } = new();
    }

    public static string ToJson(JsonElement id, int code, string message, DiagnosticRecord? data)
    {
        var envelope = new Envelope
        {
            Id = id,
            Error = new ErrorDto { Code = code, Message = message, Data = data },
        };

        return JsonSerializer.Serialize(envelope, WireJson.Compact);
    }
}
