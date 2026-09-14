using System.Runtime.InteropServices;

namespace Rhino.MCPBridge.PlugIn;

/// <summary>Finds the <c>claude</c> CLI so <c>MCPBridgeRegister</c> can shell out to it (PRD §15). PATH
/// first — but a Rhino launched from Finder/Explorer does not inherit the shell PATH, so the common
/// per-user install locations are checked too (the CLI installs to <c>~/.local/bin</c> by default; that
/// is where the phase-7 spike found it on Windows). Returns the path to the native executable, or
/// <c>null</c>, in which case the command falls back to printing the config snippet.</summary>
internal static class ClaudeCliLocator
{
    internal static string? Locate()
    {
        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "claude.exe" : "claude";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(dir)) candidates.Add(Path.Combine(dir.Trim(), exe));
            }
        }

        // Common install locations a GUI-launched process may miss.
        candidates.Add(Path.Combine(home, ".local", "bin", exe));
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            candidates.Add("/opt/homebrew/bin/" + exe);
            candidates.Add("/usr/local/bin/" + exe);
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}
