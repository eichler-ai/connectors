using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rhino.MCPBridge.Core.Registration;

/// <summary>How <c>MCPBridgeRegister</c> knows the connector stands relative to the user's Claude
/// registration (PRD §15, phase 7). The <c>rhino</c> registration is a stdio MCP server whose
/// <c>command</c> is the path to the server binary the yak package installed beside the plug-in.</summary>
public enum RegistrationState
{
    /// <summary>No <c>rhino</c> server in the config at all.</summary>
    Unregistered,

    /// <summary>A <c>rhino</c> server exists but points at a different path (an old install, a moved
    /// package) — re-registering updates it.</summary>
    StalePath,

    /// <summary>A <c>rhino</c> server exists and its command is the server binary beside this plug-in.</summary>
    Current,
}

/// <summary>
/// Pure, host-free logic for registering this connector's MCP server with a Claude client (PRD §15).
/// The plug-in's <c>MCPBridgeRegister</c> command shells out to the <c>claude</c> CLI with
/// <see cref="AddArgv"/> when it is present, and otherwise shows <see cref="SnippetJson"/> for the user
/// to paste; <see cref="StateFor"/> reads an existing config so <c>MCPBridgeStatus</c> can say whether a
/// (re-)register is needed. Everything here is deterministic and unit-tested — the command is a thin
/// shell over it.
/// </summary>
public static class ClientRegistration
{
    /// <summary>The client registration slug (CONVENTIONS.md): the connector is registered as "rhino".</summary>
    public const string ServerName = "rhino";

    /// <summary>Argv (after the executable) for <c>claude mcp add</c> registering the server at
    /// <paramref name="serverPath"/> at user scope, so it is available in every project. The <c>--</c>
    /// separates the command from claude's own flags, so a path is never parsed as one.</summary>
    public static IReadOnlyList<string> AddArgv(string serverPath) =>
        new[] { "mcp", "add", ServerName, "--scope", "user", "--", serverPath };

    /// <summary>Argv for <c>claude mcp remove</c>. The command runs this before <see cref="AddArgv"/> so
    /// re-registering a moved path replaces the old entry rather than failing on a name clash.</summary>
    public static IReadOnlyList<string> RemoveArgv() =>
        new[] { "mcp", "remove", ServerName, "--scope", "user" };

    /// <summary>The JSON block a user pastes into their Claude config when the <c>claude</c> CLI is not on
    /// PATH — the same shape <c>claude mcp add</c> writes (a stdio server under <c>mcpServers</c>).</summary>
    public static string SnippetJson(string serverPath)
    {
        var server = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = serverPath,
            ["args"] = new JsonArray(),
        };
        var root = new JsonObject { ["mcpServers"] = new JsonObject { [ServerName] = server } };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The command string registered for the <c>rhino</c> server in a Claude config document, or
    /// <c>null</c> if there is none. <paramref name="configJson"/> is the contents of the user's
    /// <c>~/.claude.json</c>; a malformed or empty document reads as "no registration", never throws.</summary>
    public static string? RegisteredCommand(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            var root = JsonNode.Parse(configJson) as JsonObject;
            if (root?["mcpServers"] is not JsonObject servers) return null;
            if (servers[ServerName] is not JsonObject server) return null;
            // Read through a JsonValue string check — a non-string `command` (number, array, object,
            // bool: a hand-edited or foreign-schema config) reads as "no registration", never throws.
            return (server["command"] as JsonValue)?.TryGetValue<string>(out var command) == true ? command : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The user-scope Claude Code config file (<c>~/.claude.json</c>) that <c>claude mcp add
    /// --scope user</c> writes and <see cref="StateFor"/> reads.</summary>
    public static string UserConfigPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    /// <summary>Whether the config registers this connector, and if so whether it points at
    /// <paramref name="serverPath"/>. Path comparison is case-insensitive on Windows (its filesystem is)
    /// and case-sensitive elsewhere.</summary>
    public static RegistrationState StateFor(string? configJson, string serverPath)
    {
        var registered = RegisteredCommand(configJson);
        if (registered is null) return RegistrationState.Unregistered;
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(registered.Trim(), serverPath.Trim(), comparison)
            ? RegistrationState.Current
            : RegistrationState.StalePath;
    }
}
