using MCPBridge.Core.Protocol;
using Xunit;

namespace MCPBridge.Core.Tests.Protocol;

public class AuthMessageTests
{
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
