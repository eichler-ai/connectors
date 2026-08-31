using System.Collections.Generic;
using System.Text.Json;
using MCPBridge.Core.Diagnostics;

namespace MCPBridge.Core.Protocol;

/// <summary>
/// Thrown by <see cref="JsonRpcRequest"/>'s param accessors (and by the discovery layer's own param
/// validation) when a required parameter is missing or the wrong shape -- a wire-level input problem a
/// caller (the dispatcher) is expected to catch and convert into a JSON-RPC error response, never let
/// propagate and kill the connection.
///
/// <para>Carries a full PRD §01 <see cref="DiagnosticRecord"/>, not just a message (issue #69). Before
/// that, every one of these reached an agent as a bare <c>InvalidParams</c> string with no `code` to
/// branch on and no `remedy`, while the Go broker's equivalent validation (e.g. describe_function's
/// `missing-required-param`) carried both -- so the SAME logical failure had two different shapes
/// depending on which side of the wire caught it first. Building the record here rather than at each of
/// the six catch sites is what makes that structural: a new throw site cannot forget, because there is no
/// constructor that omits it.</para>
/// </summary>
public sealed class JsonRpcParamException : System.Exception
{
    /// <summary>
    /// Fallback `code` for a param error whose thrower has nothing more specific to say. Deliberately
    /// generic -- prefer a concrete code (<c>missing-required-param</c>, <c>invalid-param-type</c>,
    /// <c>invalid-cursor</c>) wherever the throw site knows which of those it is.
    /// </summary>
    public const string DefaultCode = "invalid-param";

    /// <summary>The §01 record this error should reach the agent as, as the JSON-RPC error's `data`.</summary>
    public DiagnosticRecord Diagnostic { get; }

    /// <param name="source">
    /// Required, not defaulted, on purpose: §01's `source` exists so a reader can find the relevant code,
    /// and a default would silently label a discovery-layer throw as a protocol-layer one. `code` DOES
    /// default, because <see cref="DefaultCode"/> is an honest answer for a param error with no finer
    /// classification; a wrong `source` is not.
    /// </param>
    public JsonRpcParamException(
        string message,
        DiagnosticSource source,
        string code = DefaultCode,
        IReadOnlyDictionary<string, object?>? detail = null,
        IReadOnlyList<string>? remedy = null)
        : base(message)
    {
        Diagnostic = DiagnosticRecord.Create(DiagnosticSeverity.Error, code, source, message, detail, remedy);
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
            // The two Parse throws carry a §01 record, but be precise about its reach: BridgeHost's read
            // loop catches a Parse failure and SKIPS the line, so this record never reaches the agent. A
            // request with no usable `id` cannot be answered at all -- there is nothing to address a
            // response to -- which is the whole reason Parse rejects it. Its consumer is BridgeHost's
            // connection.log line instead. (When this was first written the comment claimed a "log path"
            // that did not exist -- the catch was a bare `catch { continue; }` that discarded the
            // exception. Review caught it; the log path is real now, and this sentence is only true
            // because that was fixed rather than reworded.)
            throw new JsonRpcParamException(
                "message has no non-empty string 'method' field; not a valid JSON-RPC request.",
                DiagnosticSource.Protocol,
                "malformed-request",
                detail: new Dictionary<string, object?> { ["field"] = "method" },
                remedy: new[] { "Send a JSON-RPC 2.0 request object whose 'method' is a non-empty string." });
        }

        // A request with no (or a null) id can never be answered -- ExecutionResultMessage/
        // JsonRpcErrorMessage both need a real id to echo back, and this class's own doc comment already
        // states the Go broker never sends the add-in anything but genuine requests (always a non-null
        // id). Reject it here rather than letting a default(JsonElement) id reach message serialization
        // later, where it throws InvalidOperationException from deep inside System.Text.Json instead of a
        // clear message naming the actual problem (the same failure class AuthMessage's own constructor
        // guards against for the exact same reason).
        if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new JsonRpcParamException(
                "message has no non-null 'id' field; not a valid JSON-RPC request this add-in can respond to.",
                DiagnosticSource.Protocol,
                "malformed-request",
                detail: new Dictionary<string, object?> { ["field"] = "id" },
                remedy: new[] { "Send a JSON-RPC 2.0 request with a non-null 'id'; this add-in answers requests only, never notifications." });
        }

        // .Clone() is required here: doc (and every JsonElement view into it) becomes invalid once this
        // using block disposes it at the end of this method, so anything returned to the caller must be an
        // independent copy, not a view into the disposed document.
        var id = idElement.Clone();
        var paramsElement = root.TryGetProperty("params", out var p) ? p.Clone() : default;

        return new JsonRpcRequest(id, methodElement.GetString()!, paramsElement);
    }

    /// <summary>
    /// The `params.&lt;name&gt; must be a &lt;json type&gt;` error, built in one place: four accessors raise
    /// the identical failure, and §01's value here comes from `code`/`detail` being genuinely the same
    /// across all four rather than four hand-written near-copies free to drift apart.
    /// </summary>
    /// <param name="hasDefault">
    /// Whether omitting the param is legal. This is NOT cosmetic: review of the change that introduced
    /// this helper caught it telling a caller of a REQUIRED param to "omit it entirely to take this
    /// parameter's default" -- a remedy that provably does not work, since the next call comes back
    /// `missing-required-param`. A §01 remedy that sends an agent down a dead end is worse than no
    /// remedy, and the "every remedy is a non-empty string" assertion cannot tell the two apart, so the
    /// distinction has to exist in the code rather than in the reviewer's attention.
    /// </param>
    private static JsonRpcParamException WrongType(string name, string expectedJsonType, bool hasDefault) =>
        new($"params.{name} must be a {expectedJsonType}.",
            DiagnosticSource.Protocol,
            "invalid-param-type",
            detail: new Dictionary<string, object?> { ["param"] = name, ["expected_type"] = expectedJsonType },
            remedy: hasDefault
                ? new[] { $"Pass params.{name} as a JSON {expectedJsonType}, or omit it entirely to take this parameter's default." }
                : new[] { $"Pass params.{name} as a JSON {expectedJsonType}; it is required and has no default." });

    /// <summary>Reads a required non-empty string param. Throws <see cref="JsonRpcParamException"/> if absent, empty, or the wrong JSON type.</summary>
    public string GetRequiredString(string name)
    {
        // Absent/empty and present-but-wrong-typed are reported as DIFFERENT codes even though the
        // message is the same sentence. That distinction is the point of issue #69: a caller that can
        // branch on `code` should be able to tell "you forgot this" (add the param) from "you sent the
        // wrong kind of thing" (fix the value you already have) without parsing prose.
        if (_params.ValueKind != JsonValueKind.Object || !_params.TryGetProperty(name, out var value))
        {
            throw MissingRequired(name);
        }

        // An explicit JSON `null` is ABSENT, not wrong-typed -- it is the canonical encoding of "not
        // supplied" and what a client serializing from a nullable field emits. Every optional accessor
        // below already treats Null/Undefined as absent; review caught this one classifying it as
        // `invalid-param-type`, which put the most common way of omitting a param on the wrong side of
        // the very split this change introduced.
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw MissingRequired(name);
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw WrongType(name, "string", hasDefault: false);
        }

        if (string.IsNullOrEmpty(value.GetString()))
        {
            throw MissingRequired(name);
        }

        return value.GetString()!;
    }

    private static JsonRpcParamException MissingRequired(string name) =>
        new($"params.{name} must be a non-empty string.",
            DiagnosticSource.Protocol,
            "missing-required-param",
            detail: new Dictionary<string, object?> { ["param"] = name },
            remedy: new[] { $"Pass params.{name} as a non-empty string; it has no default." });

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
            _ => throw WrongType(name, "number", hasDefault: true),
        };
    }

    /// <summary>Reads an optional 32-bit numeric param (PRD §08's page_size/top_n), returning <paramref name="defaultValue"/> if absent/null. Throws if present with a non-numeric shape.</summary>
    public int GetOptionalInt32(string name, int defaultValue) => (int)GetOptionalInt64(name, defaultValue);

    /// <summary>Reads an optional boolean param (PRD §09's overwrite_output_files), returning <paramref name="defaultValue"/> if absent/null. Throws if present with a non-boolean shape.</summary>
    public bool GetOptionalBool(string name, bool defaultValue)
    {
        if (_params.ValueKind != JsonValueKind.Object || !_params.TryGetProperty(name, out var value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
            JsonValueKind.Null or JsonValueKind.Undefined => defaultValue,
            _ => throw WrongType(name, "boolean", hasDefault: true),
        };
    }

    /// <summary>Reads an optional non-empty string param (PRD §08's namespace/type_name/cursor/member/etc.), returning null if absent/null/empty. Throws if present with a non-string shape.</summary>
    public string? GetOptionalString(string name)
    {
        if (_params.ValueKind != JsonValueKind.Object || !_params.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrEmpty(value.GetString()) ? null : value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => throw WrongType(name, "string", hasDefault: true),
        };
    }
}
