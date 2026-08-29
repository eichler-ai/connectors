using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCPBridge.Core.Protocol;

/// <summary>
/// Identifies what kind of party is presenting the `auth` request, matching the Go
/// broker's two valid wire values exactly (broker.go's Role type): "add-in" and
/// "agent-client". The Bridge, being a Revit add-in, only ever sends <see cref="AddIn"/>
/// -- modeled as a real type rather than a bare string literal so the valid vocabulary is
/// named and discoverable, not scattered as magic strings.
/// </summary>
[JsonConverter(typeof(WireEnumNameConverter<AuthRole>))]
public enum AuthRole
{
    [WireEnumName("add-in")]
    AddIn,

    [WireEnumName("agent-client")]
    AgentClient,
}

/// <summary>
/// The `auth` request that MUST be the very first message sent on any new TCP
/// connection to the broker (PRD §10). Unlike <see cref="RegisterMessage"/> (a
/// notification -- no `id`), this is a genuine JSON-RPC 2.0 *request*: it carries an
/// `id` so the broker's `{"jsonrpc":"2.0","id":...,"result":{"ok":true}}` response (or
/// a JSON-RPC error response on rejection, after which the broker closes the
/// connection) can be correlated back to it. Only after this exchange succeeds does the
/// broker expect whatever's next for the given role -- for role add-in, a `register`
/// notification (see RegisterMessage).
///
/// There was no existing request-shaped envelope to reuse here: RegisterMessage's
/// private Envelope type models a notification only (no `id` field at all), and adding
/// an optional `id` to it would blur the notification/request distinction for every
/// other notification-only message type in this namespace. A separate, small envelope
/// tailored to a request is the cleaner fit for a single, one-off handshake message.
/// </summary>
public sealed class AuthMessage
{
    private readonly JsonElement _id;
    private readonly string _token;
    private readonly AuthRole _role;

    public AuthMessage(JsonElement id, string token, AuthRole role)
    {
        // A default(JsonElement) (ValueKind.Undefined) would otherwise pass silently here and only
        // surface as an opaque InvalidOperationException from deep inside JsonSerializer.Serialize
        // when ToJson() is eventually called -- fail immediately, at construction, with a message
        // that actually names the problem. ValueKind.Null is the same failure class one step later:
        // it serializes fine here (emits "id":null), but the Go broker's IsRequest() treats a JSON-RPC
        // message with a null id as NOT a request (broker.go's authParams path requires msg.ID != nil),
        // so it gets rejected with "auth-required" and the connection closed -- an equally opaque
        // failure far from this constructor, for a value this guard can catch just as easily.
        if (id.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException("id must be a real, non-null JSON value (e.g. from JsonSerializer.SerializeToElement) -- the Go broker requires every request's id to be non-null.", nameof(id));
        }

        _id = id;
        _token = token;
        _role = role;
    }

    /// <summary>Convenience overload for the common case of an integer request id.</summary>
    public AuthMessage(int id, string token, AuthRole role)
        : this(JsonSerializer.SerializeToElement(id), token, role)
    {
    }

    private sealed class ParamsDto
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = "";

        [JsonPropertyName("role")]
        public AuthRole Role { get; set; }
    }

    private sealed class Envelope
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public JsonElement Id { get; set; }

        [JsonPropertyName("method")]
        public string Method { get; set; } = "auth";

        [JsonPropertyName("params")]
        public ParamsDto Params { get; set; } = new();
    }

    public string ToJson()
    {
        var envelope = new Envelope
        {
            Id = _id,
            Params = new ParamsDto
            {
                Token = _token,
                Role = _role,
            },
        };

        return JsonSerializer.Serialize(envelope, WireJson.Compact);
    }
}
