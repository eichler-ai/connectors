using System.Text.Json.Nodes;
using Rhino.MCPBridge.Core.Registration;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Registration;

/// <summary>
/// The registration writer/reader behind <c>MCPBridgeRegister</c> (PRD §15, phase 7). The plug-in
/// command is a thin shell over this: it shells <c>claude</c> with <see cref="ClientRegistration.AddArgv"/>,
/// falls back to <see cref="ClientRegistration.SnippetJson"/>, and reads state with
/// <see cref="ClientRegistration.StateFor"/> for <c>MCPBridgeStatus</c>. These pin the shapes the CLI and
/// the config format require.
/// </summary>
public class ClientRegistrationTests
{
    private const string Path = "/Applications/.../packages/8.0/rhino-mcp-bridge/1.0.0/mcp-server-osx";

    [Fact]
    public void AddArgv_registersRhinoAtUserScope_withPathAfterSeparator()
    {
        var argv = ClientRegistration.AddArgv(Path);

        Assert.Equal(new[] { "mcp", "add", "rhino", "--scope", "user", "--", Path }, argv);
        // The path is the last token, after "--", so claude never parses it as a flag even with a leading dash.
        Assert.Equal(Path, argv[^1]);
        Assert.Equal("--", argv[^2]);
    }

    [Fact]
    public void RemoveArgv_targetsRhinoAtUserScope()
    {
        Assert.Equal(new[] { "mcp", "remove", "rhino", "--scope", "user" }, ClientRegistration.RemoveArgv());
    }

    [Fact]
    public void SnippetJson_isValidJson_withAStdioRhinoServerAtThePath()
    {
        var snippet = ClientRegistration.SnippetJson(Path);

        var root = JsonNode.Parse(snippet)!.AsObject();
        var server = root["mcpServers"]!["rhino"]!.AsObject();
        Assert.Equal("stdio", server["type"]!.GetValue<string>());
        Assert.Equal(Path, server["command"]!.GetValue<string>());
        Assert.Empty(server["args"]!.AsArray());
    }

    [Fact]
    public void RegisteredCommand_readsTheRhinoServerCommand()
    {
        var config = /*lang=json,strict*/ """
            {"mcpServers":{"rhino":{"type":"stdio","command":"/old/path/mcp-server-osx","args":[]}}}
            """;

        Assert.Equal("/old/path/mcp-server-osx", ClientRegistration.RegisteredCommand(config));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all {")]
    [InlineData("{}")]
    [InlineData("""{"mcpServers":{}}""")]
    [InlineData("""{"mcpServers":{"revit":{"command":"/x"}}}""")] // a different connector, not rhino
    // A rhino entry whose command is not a string (hand-edited / foreign schema) must read as "no
    // registration", not throw — GetValue<string> would throw InvalidOperationException on these.
    [InlineData("""{"mcpServers":{"rhino":{"command":123}}}""")]
    [InlineData("""{"mcpServers":{"rhino":{"command":[]}}}""")]
    [InlineData("""{"mcpServers":{"rhino":{"command":{}}}}""")]
    [InlineData("""{"mcpServers":{"rhino":{"command":true}}}""")]
    [InlineData("""{"mcpServers":{"rhino":{"command":null}}}""")]
    [InlineData("""{"mcpServers":{"rhino":{"type":"stdio"}}}""")] // no command key at all
    // A project-scoped server is intentionally ignored: this reads the user scope (--scope user).
    [InlineData("""{"projects":{"/p":{"mcpServers":{"rhino":{"command":"/x"}}}}}""")]
    public void RegisteredCommand_isNullWhenAbsentOrMalformed(string? config)
    {
        Assert.Null(ClientRegistration.RegisteredCommand(config));
    }

    [Fact]
    public void StateFor_currentWhenTheRegisteredPathMatches()
    {
        // SnippetJson writes exactly a config that registers rhino at Path, so it round-trips to Current.
        var config = ClientRegistration.SnippetJson(Path);

        Assert.Equal(RegistrationState.Current, ClientRegistration.StateFor(config, Path));
    }

    [Fact]
    public void StateFor_stalePathWhenRegisteredElsewhere()
    {
        var config = """{"mcpServers":{"rhino":{"type":"stdio","command":"/some/other/mcp-server-osx","args":[]}}}""";

        Assert.Equal(RegistrationState.StalePath, ClientRegistration.StateFor(config, Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    public void StateFor_unregisteredWhenThereIsNoRhinoServer(string? config)
    {
        Assert.Equal(RegistrationState.Unregistered, ClientRegistration.StateFor(config, Path));
    }
}
