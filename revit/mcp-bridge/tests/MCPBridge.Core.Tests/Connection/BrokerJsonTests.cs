using System;
using MCPBridge.Core.Connection;
using Xunit;

namespace MCPBridge.Core.Tests.Connection;

public class BrokerJsonTests
{
    private const string ValidJson = """
        {
          "host": "10.211.55.2",
          "port": 51423,
          "pid": 9876,
          "started_at": "2026-08-25T10:00:00Z",
          "token": "s3cr3t-token"
        }
        """;

    [Fact]
    public void Parse_ReadsAllFields()
    {
        var brokerJson = BrokerJson.Parse(ValidJson);

        Assert.Equal("10.211.55.2", brokerJson.Host);
        Assert.Equal(51423, brokerJson.Port);
        Assert.Equal(9876, brokerJson.Pid);
        Assert.Equal(new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero), brokerJson.StartedAt);
        Assert.Equal("s3cr3t-token", brokerJson.Token);
    }

    [Theory]
    [InlineData("""{"port":1,"pid":1,"started_at":"2026-01-01T00:00:00Z","token":"t"}""")]
    [InlineData("""{"host":"h","pid":1,"started_at":"2026-01-01T00:00:00Z","token":"t"}""")]
    [InlineData("""{"host":"h","port":1,"started_at":"2026-01-01T00:00:00Z","token":"t"}""")]
    [InlineData("""{"host":"h","port":1,"pid":1,"token":"t"}""")]
    [InlineData("""{"host":"h","port":1,"pid":1,"started_at":"2026-01-01T00:00:00Z"}""")]
    public void Parse_MissingRequiredField_Throws(string json)
    {
        Assert.Throws<BrokerJsonParseException>(() => BrokerJson.Parse(json));
    }

    [Fact]
    public void Parse_InvalidJson_ThrowsBrokerJsonParseException()
    {
        Assert.Throws<BrokerJsonParseException>(() => BrokerJson.Parse("not json"));
    }

    [Fact]
    public void Parse_EmptyToken_Throws()
    {
        var json = """{"host":"h","port":1,"pid":1,"started_at":"2026-01-01T00:00:00Z","token":""}""";
        Assert.Throws<BrokerJsonParseException>(() => BrokerJson.Parse(json));
    }

    // v1 integrated review: GetInt32 on a numeric-but-not-int32 value threw FormatException, which
    // is not BrokerJsonParseException -- it escaped BrokerDiscovery.TryDiscover entirely and could
    // take down the connection thread (or the process) on a malformed broker.json. Every malformed
    // shape must surface as the parse exception the discovery layer actually catches.
    [Theory]
    [InlineData("""{"host":"h","port":3.5,"pid":1,"started_at":"2026-01-01T00:00:00Z","token":"t"}""")]
    [InlineData("""{"host":"h","port":99999999999,"pid":1,"started_at":"2026-01-01T00:00:00Z","token":"t"}""")]
    [InlineData("""{"host":"h","port":1,"pid":1.25,"started_at":"2026-01-01T00:00:00Z","token":"t"}""")]
    public void Parse_NumericFieldThatIsNotInt32_ThrowsParseException_NotFormatException(string json)
    {
        Assert.Throws<BrokerJsonParseException>(() => BrokerJson.Parse(json));
    }
}
