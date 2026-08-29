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
    public void TryDiscover_RemoteMode_NoSharedDrive_ReportsNotFoundWithNoAddress()
    {
        // The remote-mode fallback host:port that used to populate Address here was removed
        // (v1 integrated review): auth is mandatory and the token only exists in broker.json,
        // so a fallback address could never produce a usable connection -- BridgeHost discarded
        // it every time. Not-found now means exactly that, with no address to mislead a caller.
        var options = BrokerDiscoveryOptions.Remote(
            sharedRootUncPath: @"\\psf\connectors-does-not-exist-" + Guid.NewGuid());
        var discovery = new BrokerDiscovery(options);

        var result = discovery.TryDiscover();

        Assert.False(result.Found);
        Assert.Null(result.Address);
    }

    [Fact]
    public void TryDiscover_LocalMode_NotFound_HasNoAddress()
    {
        var options = BrokerDiscoveryOptions.Local(localAppDataRoot: _tempRoot);
        var discovery = new BrokerDiscovery(options);

        var result = discovery.TryDiscover();

        Assert.False(result.Found);
        Assert.Null(result.Address);
    }

    [Fact]
    public void TryDiscover_UnreadableBrokerJson_ReturnsNotFoundWithDiagnostic_InsteadOfThrowing()
    {
        // v1 integrated review: the read used to catch IOException only, so any other read failure
        // (UnauthorizedAccessException from an ACL hiccup or a flapping UNC share) escaped
        // TryDiscover entirely and could kill the connection thread -- or the Revit process. The
        // contract pinned here: whatever the read failure, TryDiscover answers not-found with the
        // broker-json-unreadable diagnostic; it never throws. The denial is platform-appropriate --
        // Windows (where the tier-1 suite actually runs, on the dev VM) uses an exclusive-share
        // lock; Unix (this repo's Mac side, or any future Linux CI) uses a no-read file mode, which
        // exercises the non-IOException breadth specifically.
        var options = BrokerDiscoveryOptions.Local(localAppDataRoot: _tempRoot);
        var discovery = new BrokerDiscovery(options);
        Directory.CreateDirectory(Path.GetDirectoryName(discovery.BrokerJsonPath)!);
        File.WriteAllText(discovery.BrokerJsonPath, """{"host": "h", "port": 1, "pid": 1, "started_at": "2026-01-01T00:00:00Z", "token": "t"}""");

        if (OperatingSystem.IsWindows())
        {
            using var hold = new FileStream(discovery.BrokerJsonPath, FileMode.Open, FileAccess.Read, FileShare.None);

            var result = discovery.TryDiscover();

            Assert.False(result.Found);
            Assert.NotNull(result.Diagnostic);
            Assert.Equal("broker-json-unreadable", result.Diagnostic!.Code);
        }
        else
        {
            File.SetUnixFileMode(discovery.BrokerJsonPath, UnixFileMode.None);
            try
            {
                var result = discovery.TryDiscover();

                Assert.False(result.Found);
                Assert.NotNull(result.Diagnostic);
                Assert.Equal("broker-json-unreadable", result.Diagnostic!.Code);
            }
            finally
            {
                File.SetUnixFileMode(discovery.BrokerJsonPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
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
