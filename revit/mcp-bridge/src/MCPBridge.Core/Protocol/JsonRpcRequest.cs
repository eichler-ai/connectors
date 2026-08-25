using System.Text.Json;

namespace MCPBridge.Core.Protocol;

/// <summary>
/// Thrown by <see cref="JsonRpcRequest"/>'s param accessors when a required parameter is missing or the
/// wrong shape -- a wire-level input problem a caller (the dispatcher) is expected to catch and convert
/// into a JSON-RPC error response, never let propagate and kill the connection.
/// </summary>
public sealed class JsonRpcParamException : System.Exception
{
    public JsonRpcParamException(string message) : base(message)
    {
    }
}

/// <summary>
/// Parses one incoming NDJSON line as a JSON-RPC 2.0 request (id + method + params) -- the read-side
/// counterpart to <see cref="AuthMessage"/>/<see cref="RegisterMessage"/>'s write-side envelopes. The Go
/// broker only ever sends the add-in genuine requests (execute_script/poll_execution/cancel_execution,
/// each with a non-null id it expects correlated back via the response's own id -- see
/// transport.Conn.Call on the Go side), so this type does not model the notification/response shapes at
/// all; a caller that receives something else (no "method", or "method" present but no "id") should treat
/// it as malformed input, not silently coerce it.
/// </summary>
public sealed class JsonRpcRequest
{
    /// <summary>The request's raw id, to be echoed back verbatim in the response (any JSON scalar -- number or string).</summary>
    public JsonElement Id { get; }

    public string Method { get; }

    private readonly JsonElement _params;

    private JsonRpcRequest(JsonElement id, string method, JsonElement paramsElement)
    {
        Id = id;
        Method = method;
        _params = paramsElement;
    }

    public static JsonRpcRequest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(methodElement.GetString()))
        {
            throw new JsonRpcParamException("message has no non-empty string 'method' field; not a valid JSON-RPC request.");
        }

        // .Clone() is required here: doc (and every JsonElement view into it) becomes invalid once this
        // using block disposes it at the end of this method, so anything returned to the caller must be an
        // independent copy, not a view into the disposed document.
        var id = root.TryGetProperty("id", out var idElement) ? idElement.Clone() : default;
        var paramsElement = root.TryGetProperty("params", out var p) ? p.Clone() : default;

        return new JsonRpcRequest(id, methodElement.GetString()!, paramsElement);
    }

    /// <summary>Reads a required non-empty string param. Throws <see cref="JsonRpcParamException"/> if absent, empty, or the wrong JSON type.</summary>
    public string GetRequiredString(string name)
    {
        if (_params.ValueKind != JsonValueKind.Object ||
            !_params.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(value.GetString()))
        {
            throw new JsonRpcParamException($"params.{name} must be a non-empty string.");
        }

        return value.GetString()!;
    }

    /// <summary>Reads an optional numeric param, returning <paramref name="defaultValue"/> if absent/null. Throws if present with a non-numeric shape.</summary>
    public long GetOptionalInt64(string name, long defaultValue)
    {
        if (_params.ValueKind != JsonValueKind.Object || !_params.TryGetProperty(name, out var value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt64(),
            JsonValueKind.Null or JsonValueKind.Undefined => defaultValue,
            _ => throw new JsonRpcParamException($"params.{name} must be a number."),
        };
    }
}
