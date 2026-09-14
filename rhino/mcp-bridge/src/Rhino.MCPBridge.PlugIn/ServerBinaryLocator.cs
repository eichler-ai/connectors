using Rhino.MCPBridge.Core.Connection;
using Rhino.MCPBridge.Core.Registration;

namespace Rhino.MCPBridge.PlugIn;

/// <summary>Finds the Go MCP server binary this plug-in registers with Claude (PRD §15, phase 7). In a
/// yak install the binary sits beside the plug-in assembly under a <see cref="ServerBinaryNames"/> name;
/// a dev build (deploy-plugin.sh) has no binary there, so <see cref="OverrideEnvVar"/> points the command
/// at a locally-built server for the dev/harness loop.</summary>
internal static class ServerBinaryLocator
{
    internal const string OverrideEnvVar = "RHINO_MCP_SERVER_PATH";

    /// <summary>The server binary path, or <c>null</c> if neither the override nor a binary beside the
    /// plug-in exists (a dev build with no override — the caller tells the user to set it).</summary>
    internal static string? Locate()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;

        var name = ServerBinaryNames.ForPlatform(AppDataPaths.PlatformName());
        if (name is null) return null;
        var dir = Path.GetDirectoryName(typeof(ServerBinaryLocator).Assembly.Location);
        if (string.IsNullOrEmpty(dir)) return null;
        var candidate = Path.Combine(dir, name);
        return File.Exists(candidate) ? candidate : null;
    }
}
