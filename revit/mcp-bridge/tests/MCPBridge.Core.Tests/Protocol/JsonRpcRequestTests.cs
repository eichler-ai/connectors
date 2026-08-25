using System.Text.Json;
using MCPBridge.Core.Protocol;
using Xunit;

namespace MCPBridge.Core.Tests.Protocol;

public class JsonRpcRequestTests
{
    [Fact]
    public void Parse_ExecuteScriptRequest_ExposesMethodIdAndParams()
    {
        var json = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"execute_script\",\"params\":{\"execution_id\":\"exec-1\",\"script\":\"1+1\",\"timeout_ms\":30000,\"max_duration_ms\":600000}}";

        var request = JsonRpcRequest.Parse(json);

        Assert.Equal("execute_script", request.Method);
        Assert.Equal(JsonValueKind.Number, request.Id.ValueKind);
        Assert.Equal(7, request.Id.GetInt32());
        Assert.Equal("exec-1", request.GetRequiredString("execution_id"));
        Assert.Equal("1+1", request.GetRequiredString("script"));
        Assert.Equal(30000L, request.GetOptionalInt64("timeout_ms", -1));
        Assert.Equal(600000L, request.GetOptionalInt64("max_duration_ms", -1));
    }

    [Fact]
    public void Parse_MissingMethod_Throws()
    {
        Assert.Throws<JsonRpcParamException>(() => JsonRpcRequest.Parse("{\"jsonrpc\":\"2.0\",\"id\":1}"));
    }

    [Fact]
    public void GetRequiredString_Missing_Throws()
    {
        var request = JsonRpcRequest.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"poll_execution\",\"params\":{}}");

        Assert.Throws<JsonRpcParamException>(() => request.GetRequiredString("execution_id"));
    }

    [Fact]
    public void GetRequiredString_Empty_Throws()
    {
        var request = JsonRpcRequest.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"poll_execution\",\"params\":{\"execution_id\":\"\"}}");

        Assert.Throws<JsonRpcParamException>(() => request.GetRequiredString("execution_id"));
    }

    [Fact]
    public void GetOptionalInt64_Missing_ReturnsDefault()
    {
        var request = JsonRpcRequest.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"poll_execution\",\"params\":{}}");

        Assert.Equal(42L, request.GetOptionalInt64("timeout_ms", 42));
    }

    [Fact]
    public void GetOptionalInt64_WrongType_Throws()
    {
        var request = JsonRpcRequest.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"poll_execution\",\"params\":{\"timeout_ms\":\"soon\"}}");

        Assert.Throws<JsonRpcParamException>(() => request.GetOptionalInt64("timeout_ms", 42));
    }

    [Fact]
    public void Parse_NoParams_RequiredStringStillThrowsCleanly()
    {
        var request = JsonRpcRequest.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"cancel_execution\"}");

        Assert.Throws<JsonRpcParamException>(() => request.GetRequiredString("execution_id"));
    }

    [Fact]
    public void Parse_StringId_RoundTrips()
    {
        var request = JsonRpcRequest.Parse("{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"method\":\"poll_execution\",\"params\":{}}");

        Assert.Equal(JsonValueKind.String, request.Id.ValueKind);
        Assert.Equal("abc", request.Id.GetString());
    }
}
