using System;
using System.Text.Json;
using MCPBridge.Core.Protocol;
using Xunit;

namespace MCPBridge.Core.Tests.Protocol;

public class AuthMessageTests
{
    [Fact]
    public void Constructor_DefaultJsonElement_ThrowsImmediately_NotDeepInsideToJson()
    {
        // Fourth review finding: a default(JsonElement) (ValueKind.Undefined) used to pass silently
        // into the constructor and only surface as an opaque InvalidOperationException from
        // System.Text.Json internals when ToJson() was eventually called -- fail at construction,
        // with a message that names the actual problem.
        Assert.Throws<ArgumentException>(() => new AuthMessage(default(JsonElement), "tok", AuthRole.AddIn));
    }

    [Fact]
    public void Constructor_NullJsonElement_ThrowsImmediately_NotAsAnOpaqueConnectionClose()
    {
        // Same failure class as the Undefined case, one step later: a JSON-RPC id of `null`
        // serializes without error here, but the Go broker's IsRequest() treats a null id as "not a
        // request" and rejects it with auth_required, closing the connection -- catch it here instead.
        var nullElement = JsonSerializer.SerializeToElement<object?>(null);

        Assert.Throws<ArgumentException>(() => new AuthMessage(nullElement, "tok", AuthRole.AddIn));
    }

    [Fact]
    public void ToJson_IsARequest_WithIdMethodTokenAndRole()
    {
        // Fix 3: the very first message on any new connection must be a JSON-RPC 2.0
        // *request* (has an id) shaped exactly like
        // {"jsonrpc":"2.0","id":<...>,"method":"auth","params":{"token":"...","role":"add-in"}}.
        var message = new AuthMessage(id: 1, token: "s3cr3t-token", role: AuthRole.AddIn);

        var json = message.ToJson();

        Assert.Contains("\"jsonrpc\":\"2.0\"", json);
        Assert.Contains("\"id\":1", json);
        Assert.Contains("\"method\":\"auth\"", json);
        Assert.Contains("\"token\":\"s3cr3t-token\"", json);
        Assert.Contains("\"role\":\"add-in\"", json);
    }

    [Fact]
    public void ToJson_Role_AgentClient_SerializesAsAgentClientString()
    {
        var message = new AuthMessage(id: 2, token: "tok", role: AuthRole.AgentClient);

        var json = message.ToJson();

        Assert.Contains("\"role\":\"agent-client\"", json);
    }

    [Fact]
    public void ToJson_IsSingleLine_SafeForNdjsonFraming()
    {
        var message = new AuthMessage(id: 1, token: "tok", role: AuthRole.AddIn);

        var json = message.ToJson();

        Assert.DoesNotContain("\n", json);
    }
}
