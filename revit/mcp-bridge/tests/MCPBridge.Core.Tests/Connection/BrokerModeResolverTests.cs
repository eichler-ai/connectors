using System;
using System.Collections.Generic;
using System.IO;
using MCPBridge.Core.Connection;
using Xunit;

namespace MCPBridge.Core.Tests.Connection;

/// <summary>
/// Pins issue #185's precedence table: bridge-config.json → MCPBRIDGE_BROKER_MODE/MCPBRIDGE_SHARED_ROOT
/// → Local. Each test names the row it pins; the leftover-env case is the one the issue was filed over.
/// </summary>
public class BrokerModeResolverTests
{
    private static Func<string, string?> Env(params (string Name, string? Value)[] vars)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in vars)
        {
            map[name] = value;
        }

        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    private static readonly Func<string, string?> NoEnv = Env();

    [Fact]
    public void NoConfig_NoEnv_DefaultsToLocal()
    {
        var resolution = BrokerModeResolver.Resolve(config: null, NoEnv);

        Assert.Equal(BrokerTopologyMode.Local, resolution.Options.Mode);
        Assert.Equal(BrokerModeResolver.DecisionSource.Default, resolution.Source);
        Assert.Null(resolution.Diagnostic);
    }

    [Fact]
    public void NoConfig_EnvRemoteWithRoot_IsRemote_FromEnvironment()
    {
        // The pre-#185 mechanism, unchanged: the dev launchers keep working.
        var env = Env(("MCPBRIDGE_BROKER_MODE", "remote"), ("MCPBRIDGE_SHARED_ROOT", @"\\Mac\connectors"));

        var resolution = BrokerModeResolver.Resolve(config: null, env);

        Assert.Equal(BrokerTopologyMode.Remote, resolution.Options.Mode);
        Assert.Equal(BrokerModeResolver.DecisionSource.Environment, resolution.Source);
        Assert.Equal(@"\\Mac\connectors\Connectors\Revit", resolution.Options.ConnectorRoot);
    }

    [Fact]
    public void NoConfig_EnvRemoteCaseInsensitive()
    {
        var env = Env(("MCPBRIDGE_BROKER_MODE", "Remote"), ("MCPBRIDGE_SHARED_ROOT", @"\\Mac\connectors"));

        Assert.Equal(BrokerTopologyMode.Remote, BrokerModeResolver.Resolve(null, env).Options.Mode);
    }

    [Fact]
    public void ConfigLocal_OverridesLeftoverEnvRemote()
    {
        // THE #185 case: a stale MCPBRIDGE_BROKER_MODE=remote in the environment used to force a
        // correct local install onto the Mac broker. One ribbon click writes brokerMode=local and the
        // environment must stop mattering -- including its shared root.
        var config = new BridgeConfig { BrokerMode = "local" };
        var env = Env(("MCPBRIDGE_BROKER_MODE", "remote"), ("MCPBRIDGE_SHARED_ROOT", @"\\Mac\connectors"));

        var resolution = BrokerModeResolver.Resolve(config, env);

        Assert.Equal(BrokerTopologyMode.Local, resolution.Options.Mode);
        Assert.Equal(BrokerModeResolver.DecisionSource.Config, resolution.Source);
        Assert.Null(resolution.Diagnostic);
    }

    [Fact]
    public void ConfigRemoteWithRoot_OverridesEnvLocalOrUnset()
    {
        var config = new BridgeConfig { BrokerMode = "REMOTE", SharedRoot = @"\\Mac\connectors" };

        var resolution = BrokerModeResolver.Resolve(config, NoEnv);

        Assert.Equal(BrokerTopologyMode.Remote, resolution.Options.Mode);
        Assert.Equal(BrokerModeResolver.DecisionSource.Config, resolution.Source);
        Assert.Equal(@"\\Mac\connectors\Connectors\Revit", resolution.Options.ConnectorRoot);
    }

    [Fact]
    public void ConfigRemote_RootPrefersConfig_OverEnv()
    {
        var config = new BridgeConfig { BrokerMode = "remote", SharedRoot = @"\\Mac\fromconfig" };
        var env = Env(("MCPBRIDGE_SHARED_ROOT", @"\\Mac\fromenv"));

        var resolution = BrokerModeResolver.Resolve(config, env);

        Assert.StartsWith(@"\\Mac\fromconfig", resolution.Options.ConnectorRoot);
    }

    [Fact]
    public void ConfigRemote_NoConfigRoot_FallsBackToEnvRoot()
    {
        var config = new BridgeConfig { BrokerMode = "remote" };
        var env = Env(("MCPBRIDGE_SHARED_ROOT", @"\\Mac\fromenv"));

        var resolution = BrokerModeResolver.Resolve(config, env);

        Assert.Equal(BrokerTopologyMode.Remote, resolution.Options.Mode);
        Assert.StartsWith(@"\\Mac\fromenv", resolution.Options.ConnectorRoot);
        Assert.Null(resolution.Diagnostic);
    }

    [Fact]
    public void ConfigRemote_NoRootAnywhere_FallsBackToLocal_WithDiagnostic()
    {
        var config = new BridgeConfig { BrokerMode = "remote" };

        var resolution = BrokerModeResolver.Resolve(config, NoEnv);

        Assert.Equal(BrokerTopologyMode.Local, resolution.Options.Mode);
        Assert.Equal(BrokerModeResolver.DecisionSource.Config, resolution.Source);
        Assert.NotNull(resolution.Diagnostic);
        Assert.Equal("broker-mode-remote-unusable", resolution.Diagnostic!.Code);
        Assert.Equal("mcp-bridge.core.connection", resolution.Diagnostic.Source);
        Assert.NotEmpty(resolution.Diagnostic.Remedy);
    }

    [Fact]
    public void Remote_MappedDriveRoot_FallsBackToLocal_WithDiagnostic()
    {
        // BrokerDiscoveryOptions.Remote()'s UNC rule (PRD §09) still applies; the resolver turns the
        // ArgumentException into the same never-throw fallback BuildDiscoveryOptions always had.
        var env = Env(("MCPBRIDGE_BROKER_MODE", "remote"), ("MCPBRIDGE_SHARED_ROOT", @"Z:\connectors"));

        var resolution = BrokerModeResolver.Resolve(null, env);

        Assert.Equal(BrokerTopologyMode.Local, resolution.Options.Mode);
        Assert.Equal(BrokerModeResolver.DecisionSource.Environment, resolution.Source);
        Assert.Equal("broker-mode-remote-unusable", resolution.Diagnostic!.Code);
        Assert.Contains("Z:", resolution.Diagnostic.Message);
    }

    [Fact]
    public void ConfigWithBlankMode_IsNotADecision_EnvStillApplies()
    {
        // A file kept only to remember sharedRoot must not silently pin the mode.
        var config = new BridgeConfig { BrokerMode = "  ", SharedRoot = @"\\Mac\connectors" };
        var env = Env(("MCPBRIDGE_BROKER_MODE", "remote"), ("MCPBRIDGE_SHARED_ROOT", @"\\Mac\connectors"));

        var resolution = BrokerModeResolver.Resolve(config, env);

        Assert.Equal(BrokerTopologyMode.Remote, resolution.Options.Mode);
        Assert.Equal(BrokerModeResolver.DecisionSource.Environment, resolution.Source);
    }

    [Fact]
    public void SharedRoot_IsTrimmed()
    {
        var config = new BridgeConfig { BrokerMode = "remote", SharedRoot = @"  \\Mac\connectors  " };

        var resolution = BrokerModeResolver.Resolve(config, NoEnv);

        Assert.Equal(@"\\Mac\connectors\Connectors\Revit", resolution.Options.ConnectorRoot);
    }
}

public class BridgeConfigTests : IDisposable
{
    private readonly string _tempRoot;

    public BridgeConfigTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mcpbridge-config-tests-" + Guid.NewGuid());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private string ConfigPath => Path.Combine(_tempRoot, "Connectors", "Revit", "bridge-config.json");

    [Fact]
    public void Load_MissingFile_IsAbsent_NoDiagnostic()
    {
        var result = BridgeConfig.Load(ConfigPath);

        Assert.Null(result.Config);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public void Save_CreatesDirectory_AndRoundTrips()
    {
        var config = new BridgeConfig { BrokerMode = "remote", SharedRoot = @"\\Mac\connectors" };

        config.Save(ConfigPath);
        var loaded = BridgeConfig.Load(ConfigPath);

        Assert.NotNull(loaded.Config);
        Assert.Equal("remote", loaded.Config!.BrokerMode);
        Assert.Equal(@"\\Mac\connectors", loaded.Config.SharedRoot);
        Assert.False(File.Exists(ConfigPath + ".tmp"), "the temp file must be moved into place, not left behind");
    }

    [Fact]
    public void Save_OverwritesExisting()
    {
        new BridgeConfig { BrokerMode = "remote", SharedRoot = @"\\Mac\connectors" }.Save(ConfigPath);
        new BridgeConfig { BrokerMode = "local", SharedRoot = @"\\Mac\connectors" }.Save(ConfigPath);

        var loaded = BridgeConfig.Load(ConfigPath).Config!;

        Assert.Equal("local", loaded.BrokerMode);
        Assert.Equal(@"\\Mac\connectors", loaded.SharedRoot); // kept for the next switch's default
    }

    [Fact]
    public void Load_IsCaseInsensitive_AndTolerantOfHandEdits()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, """
            {
              // hand-edited
              "BrokerMode": "remote",
              "sharedroot": "\\\\Mac\\connectors",
            }
            """);

        var loaded = BridgeConfig.Load(ConfigPath).Config;

        Assert.NotNull(loaded);
        Assert.True(loaded!.IsRemote);
        Assert.Equal(@"\\Mac\connectors", loaded.SharedRoot);
    }

    [Fact]
    public void Load_MalformedJson_IsAbsent_WithDiagnostic()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, "{not json");

        var result = BridgeConfig.Load(ConfigPath);

        Assert.Null(result.Config);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal("bridge-config-unreadable", result.Diagnostic!.Code);
        Assert.Contains(ConfigPath, result.Diagnostic.Message);
    }

    [Fact]
    public void Load_EmptyFile_IsAbsent_NoDiagnostic()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, "");

        var result = BridgeConfig.Load(ConfigPath);

        Assert.Null(result.Config);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public void Save_OmitsNullSharedRoot()
    {
        new BridgeConfig { BrokerMode = "local" }.Save(ConfigPath);

        var json = File.ReadAllText(ConfigPath);

        Assert.Contains("\"brokerMode\": \"local\"", json);
        Assert.DoesNotContain("sharedRoot", json);
    }
}
