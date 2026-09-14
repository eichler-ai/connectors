using Rhino.MCPBridge.Core.Registration;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Registration;

/// <summary>Pins the server-binary filenames the packaging script and the plug-in's locator both depend
/// on (PRD §15). A rename here is a deliberate change that the dev-tooling packaging must match.</summary>
public class ServerBinaryNamesTests
{
    [Theory]
    [InlineData("windows", "mcp-server-win-x64.exe")]
    [InlineData("macos", "mcp-server-mac")]
    public void ForPlatform_mapsPlatformNameToBinaryFilename(string platform, string expected)
    {
        Assert.Equal(expected, ServerBinaryNames.ForPlatform(platform));
    }

    [Theory]
    [InlineData("linux")]
    [InlineData("")]
    [InlineData("MACOS")] // case-sensitive: the platform strings are lowercase, as AppDataPaths reports them
    public void ForPlatform_isNullForPlatformsWithNoServerBuild(string platform)
    {
        Assert.Null(ServerBinaryNames.ForPlatform(platform));
    }
}
