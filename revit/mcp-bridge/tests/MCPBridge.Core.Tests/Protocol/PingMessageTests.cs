using MCPBridge.Core.Protocol;
using Xunit;

namespace MCPBridge.Core.Tests.Protocol;

public class PingMessageTests
{
    [Fact]
    public void ToJson_IsANotification_WithMethodPingAndNoId()
    {
        // PRD §05 heartbeat: must be a notification (no "id"), not a request -- the Go
        // broker's IsNotification() classifies wire messages on exactly that distinction
        // (transport/rpc.go), and a stray "id" here would misroute it as a request the
        // broker expects a response to.
        var json = PingMessage.ToJson();

        Assert.Contains("\"jsonrpc\":\"2.0\"", json);
        Assert.Contains("\"method\":\"ping\"", json);
        Assert.DoesNotContain("\"id\"", json);
    }

    [Fact]
    public void ToJson_HasNoParams()
    {
        var json = PingMessage.ToJson();

        Assert.DoesNotContain("\"params\"", json);
    }

    [Fact]
    public void ToJson_IsSingleLine_SafeForNdjsonFraming()
    {
        var json = PingMessage.ToJson();

        Assert.DoesNotContain("\n", json);
    }
}
