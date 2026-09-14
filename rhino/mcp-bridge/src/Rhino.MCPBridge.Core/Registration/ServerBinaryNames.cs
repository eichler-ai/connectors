namespace Rhino.MCPBridge.Core.Registration;

/// <summary>
/// The filename of the Go MCP server binary the yak package installs beside the plug-in, per platform
/// (PRD §15, phase 7). A single "-any" yak package carries both platforms' binaries under these names;
/// the plug-in's <c>ServerBinaryLocator</c> picks the one for its OS to register with Claude.
///
/// The packaging step (dev-tooling) writes the binaries under these exact names — the two MUST agree, so
/// this is the one source of truth for them.
/// </summary>
public static class ServerBinaryNames
{
    /// <summary>Windows x64 (Rhino for Windows is x64, natively or under emulation on ARM).</summary>
    public const string Windows = "mcp-server-win-x64.exe";

    /// <summary>macOS universal binary (arm64 + x64), so one name serves both Mac architectures.</summary>
    public const string MacOs = "mcp-server-mac";

    /// <summary>The server binary filename for a platform as <c>AppDataPaths.PlatformName()</c> reports it
    /// ("windows" / "macos"), or <c>null</c> for a platform with no server build (e.g. "linux").</summary>
    public static string? ForPlatform(string platformName) => platformName switch
    {
        "windows" => Windows,
        "macos" => MacOs,
        _ => null,
    };
}
