using System.Runtime.InteropServices;

namespace Rhino.MCPBridge.Core.Connection;

/// <summary>
/// Where this connector keeps its per-machine state (CONVENTIONS.md "App-data layout"): the platform's
/// local app-data directory plus <c>Connectors/Rhino</c>. Spelled out per platform rather than taken
/// from <see cref="Environment.SpecialFolder.LocalApplicationData"/>, because .NET maps that to
/// <c>~/.local/share</c> on macOS while the Go server (os.UserConfigDir) resolves
/// <c>~/Library/Application Support</c> — and the two sides MUST agree on where instances/&lt;pid&gt;.json
/// lives or the server never finds the plug-in. The rule here is mirrored in the server's
/// internal/appdata; change both.
/// </summary>
public static class AppDataPaths
{
    public static string ConnectorRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
            return Path.Combine(local, "Connectors", "Rhino");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Connectors", "Rhino");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = string.IsNullOrEmpty(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share") : xdg;
        return Path.Combine(baseDir, "Connectors", "Rhino");
    }

    /// <summary>The directory of instances/&lt;pid&gt;.json files every server scans (PRD §05).</summary>
    public static string InstancesDir(string? connectorRoot = null) => Path.Combine(connectorRoot ?? ConnectorRoot(), "instances");

    /// <summary>"macos" | "windows" | "linux" as `register` and the instance file report it.</summary>
    public static string PlatformName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
        return "linux";
    }
}
