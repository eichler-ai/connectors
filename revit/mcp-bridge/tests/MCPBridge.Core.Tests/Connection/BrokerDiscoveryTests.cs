using System;
using System.IO;
using MCPBridge.Core.Connection;
using Xunit;

namespace MCPBridge.Core.Tests.Connection;

public class BrokerDiscoveryTests : IDisposable
{
    private readonly string _tempRoot;

    public BrokerDiscoveryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mcpbridge-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void LocalMode_BrokerJsonPath_IsUnderConnectorsRevitAppData()
    {
        var options = BrokerDiscoveryOptions.Local(localAppDataRoot: _tempRoot);
        var discovery = new BrokerDiscovery(options);

        var path = discovery.BrokerJsonPath;

        Assert.Equal(Path.Combine(_tempRoot, "Connectors", "Revit", "broker.json"), path);
    }

    [Fact]
    public void RemoteMode_BrokerJsonPath_IsUncPath_NotMappedDrive()
    {
        // PRD §09: remote-mode discovery must use the UNC form (\\psf\connectors\...),
        // never a mapped drive letter, since letter assignment isn't guaranteed stable.
        var options = BrokerDiscoveryOptions.Remote(sharedRootUncPath: @"\\psf\connectors");
        var discovery = new BrokerDiscovery(options);

        var path = discovery.BrokerJsonPath;

        Assert.Equal(@"\\psf\connectors\Connectors\Revit\broker.json", path);
        Assert.StartsWith(@"\\", path);
    }

    [Fact]
    public void RemoteMode_RejectsMappedDriveLetterRoot()
    {
        Assert.Throws<ArgumentException>(() => BrokerDiscoveryOptions.Remote(sharedRootUncPath: @"Z:\connectors"));
    }

    [Fact]
    public void TryDiscover_FileMissing_ReturnsNotFound()
    {
        var options = BrokerDiscoveryOptions.Local(localAppDataRoot: _tempRoot);
        var discovery = new BrokerDiscovery(options);

        var result = discovery.TryDiscover();

        Assert.False(result.Found);
        Assert.Null(result.BrokerJson);
    }

    [Fact]
    public void TryDiscover_FilePresent_ParsesAndReturnsFound()
    {
        var options = BrokerDiscoveryOptions.Local(localAppDataRoot: _tempRoot);
        var discovery = new BrokerDiscovery(options);
        Directory.CreateDirectory(Path.GetDirectoryName(discovery.BrokerJsonPath)!);
        File.WriteAllText(discovery.BrokerJsonPath, """
            {"host": "127.0.0.1", "port": 4000, "pid": 1, "started_at": "2026-01-01T00:00:00Z", "token": "tok"}
            """);

        var result = discovery.TryDiscover();

        Assert.True(result.Found);
        Assert.Equal(4000, result.BrokerJson!.Port);
    }

    [Fact]
    public void TryDiscover_FilePresent_SurfacesAddress_FromParsedBrokerJson()
    {
        // Fix 2: a successful discovery must also yield a usable Address (host+port) --
        // not just on the fallback path -- since remote mode is the primary topology and
        // has no other source of a connectable host.
        var options = BrokerDiscoveryOptions.Local(localAppDataRoot: _tempRoot);
        var discovery = new BrokerDiscovery(options);
        Directory.CreateDirectory(Path.GetDirectoryName(discovery.BrokerJsonPath)!);
        File.WriteAllText(discovery.BrokerJsonPath, """
            {"host": "10.211.55.2", "port": 4000, "pid": 1, "started_at": "2026-01-01T00:00:00Z", "token": "tok"}
            """);

        var result = discovery.TryDiscover();

        Assert.True(result.Found);
        Assert.NotNull(result.Address);
        Assert.Equal("10.211.55.2", result.Address!.Host);
        Assert.Equal(4000, result.Address.Port);
    }

    [Fact]
    public void TryDiscover_RemoteMode_NoSharedDrive_FallsBackToConfiguredHostPort()
    {
        var options = BrokerDiscoveryOptions.Remote(
            sharedRootUncPath: @"\\psf\connectors-does-not-exist-" + Guid.NewGuid(),
            fallbackHost: "10.211.55.2",
            fallbackPort: 51423);
        var discovery = new BrokerDiscovery(options);

        var result = discovery.TryDiscover();

        Assert.False(result.Found);
        Assert.NotNull(result.Fallback);
        Assert.Equal("10.211.55.2", result.Fallback!.Host);
        Assert.Equal(51423, result.Fallback.Port);
        Assert.Same(result.Fallback, result.Address);
    }

    [Fact]
    public void TryDiscover_LocalMode_NoFallbackConfigured()
    {
        var options = BrokerDiscoveryOptions.Local(localAppDataRoot: _tempRoot);
        var discovery = new BrokerDiscovery(options);

        var result = discovery.TryDiscover();

        Assert.Null(result.Fallback);
    }

    [Fact]
    public void CorruptBrokerJson_TryDiscover_ReturnsNotFoundWithDiagnostic()
    {
        var options = BrokerDiscoveryOptions.Local(localAppDataRoot: _tempRoot);
        var discovery = new BrokerDiscovery(options);
        Directory.CreateDirectory(Path.GetDirectoryName(discovery.BrokerJsonPath)!);
        File.WriteAllText(discovery.BrokerJsonPath, "{not valid json");

        var result = discovery.TryDiscover();

        Assert.False(result.Found);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal("mcp-bridge.core.connection", result.Diagnostic!.Source);
    }
}
