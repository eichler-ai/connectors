using System.Text.Json;
using Rhino.MCPBridge.Core.Protocol;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Protocol;

/// <summary>PRD §13: the first line must be an `auth` request with this instance's token. Each case
/// pins one shape of wrong first line and the code it gets; the codes are the wire contract the
/// server's dialer matches on.</summary>
public sealed class AuthHandshakeTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Auth(string token, string role = "agent-client", string? extra = null) =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"auth\",\"params\":{{\"token\":\"{token}\",\"role\":\"{role}\"{(extra is null ? "" : "," + extra)}}}}}";

    [Fact]
    public void ValidAuth_IsAccepted_WithOkResultEchoingTheId()
    {
        var r = AuthHandshake.Evaluate(Auth(Token, extra: "\"server_id\":\"srv-1\",\"server_version\":\"dev\""), Token);
        Assert.True(r.Accepted);
        Assert.Null(r.RejectionCode);
        Assert.Equal("srv-1", r.ServerId);
        Assert.Equal("dev", r.ServerVersion);
        using var doc = JsonDocument.Parse(r.ResponseJson);
        Assert.Equal(1, doc.RootElement.GetProperty("id").GetInt32());
        Assert.True(doc.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void ServerIdIsOptional()
    {
        var r = AuthHandshake.Evaluate(Auth(Token), Token);
        Assert.True(r.Accepted);
        Assert.Null(r.ServerId);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"auth\",\"params\":{}}")] // notification: no id
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"execute_script\",\"params\":{}}")] // wrong method
    public void AnythingButAnAuthRequest_IsRejected_AuthRequired(string line)
    {
        var r = AuthHandshake.Evaluate(line, Token);
        Assert.False(r.Accepted);
        Assert.Equal("auth-required", r.RejectionCode);
        AssertErrorLine(r.ResponseJson, "auth-required");
    }

    [Fact]
    public void MissingToken_IsRejected_AuthMalformed()
    {
        var r = AuthHandshake.Evaluate("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"auth\",\"params\":{\"role\":\"agent-client\"}}", Token);
        Assert.Equal("auth-malformed", r.RejectionCode);
        AssertErrorLine(r.ResponseJson, "auth-malformed");
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeF")] // one char off
    public void WrongToken_IsRejected_InvalidToken(string presented)
    {
        var r = AuthHandshake.Evaluate(Auth(presented), Token);
        Assert.False(r.Accepted);
        Assert.Equal("auth-invalid-token", r.RejectionCode);
    }

    [Fact]
    public void EmptyPresentedToken_IsMalformed_NotAMatch()
    {
        // An empty string fails the required-param check before the comparison; either way it is refused.
        var r = AuthHandshake.Evaluate(Auth(""), Token);
        Assert.False(r.Accepted);
        Assert.Equal("auth-malformed", r.RejectionCode);
    }

    [Fact]
    public void EmptyExpectedToken_NeverMatches()
    {
        // A plug-in that failed to mint a token must not be open to a blank presented token.
        Assert.False(AuthHandshake.TokensMatch("", ""));
        Assert.False(AuthHandshake.TokensMatch("", "x"));
    }

    [Theory]
    [InlineData("add-in")]
    [InlineData("AGENT-CLIENT")]
    public void WrongRole_IsRejected_InvalidRole(string role)
    {
        var r = AuthHandshake.Evaluate(Auth(Token, role), Token);
        Assert.Equal("auth-invalid-role", r.RejectionCode);
    }

    [Fact]
    public void RejectionCarriesTheRequestIdWhenThereIsOne()
    {
        var r = AuthHandshake.Evaluate(Auth("wrong"), Token);
        using var doc = JsonDocument.Parse(r.ResponseJson);
        Assert.Equal(1, doc.RootElement.GetProperty("id").GetInt32());
    }

    private static void AssertErrorLine(string json, string code)
    {
        using var doc = JsonDocument.Parse(json);
        var err = doc.RootElement.GetProperty("error");
        Assert.Equal(code, err.GetProperty("data").GetProperty("code").GetString());
        Assert.Equal("mcp-bridge.core.connection", err.GetProperty("data").GetProperty("source").GetString());
        Assert.NotEmpty(err.GetProperty("data").GetProperty("remedy").EnumerateArray());
    }
}
