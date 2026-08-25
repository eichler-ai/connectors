using System;
using Autodesk.Revit.UI;
using MCPBridge.Core.Connection;
using MCPBridge.Core.Execution;

namespace MCPBridge.AddIn;

/// <summary>
/// The add-in's entry point (PRD §04: "the add-in stays intentionally thin"). Wires
/// OnStartup/OnShutdown and delegates everything else to MCPBridge.Core -- no
/// protocol, threading, or execution decision logic lives here.
/// </summary>
public sealed class MCPBridgeApplication : IExternalApplication
{
    /// <summary>Minted once per Revit process at OnStartup; stable for the process's lifetime (PRD §05).</summary>
    public static Guid InstanceId { get; private set; }

    private BridgeHost? _host;

    public Result OnStartup(UIControlledApplication application)
    {
        InstanceId = Guid.NewGuid();

        var ringBuffer = ExecutionRingBuffer.CreateDefault();
        var executionManager = ExecutionManager.CreateDefault(ringBuffer);

        // e.g. "2027" -- available directly off ControlledApplication, no live UIApplication needed
        // (unlike the open-documents list, which register also needs -- see DocumentSnapshotHandler).
        var revitVersion = application.ControlledApplication.VersionNumber;
        var discoveryOptions = BuildDiscoveryOptions();

        _host = new BridgeHost(InstanceId, executionManager, ReconnectBackoffPolicy.Default, revitVersion, discoveryOptions);
        _host.Start();

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _host?.Stop();
        _host = null;
        return Result.Succeeded;
    }

    /// <summary>
    /// Local mode (PRD §05: "the real target deployment") is the default. Remote mode -- needed for this
    /// project's own Mac+Parallels dev setup, where the broker and Revit are on different machines -- is
    /// opt-in via environment variables, since there's no other configuration mechanism in this add-in
    /// yet: MCPBRIDGE_BROKER_MODE=remote plus MCPBRIDGE_SHARED_ROOT (a UNC path, e.g.
    /// \\psf\connectors), with MCPBRIDGE_FALLBACK_HOST/MCPBRIDGE_FALLBACK_PORT optionally supplying the
    /// remote-mode fallback address PRD §05 describes for when no shared drive is reachable. Falls back to
    /// local mode on any misconfiguration (missing shared root, unparseable port) rather than throwing out
    /// of OnStartup and failing the whole add-in load over a topology setting.
    /// </summary>
    private static BrokerDiscoveryOptions BuildDiscoveryOptions()
    {
        var mode = Environment.GetEnvironmentVariable("MCPBRIDGE_BROKER_MODE");
        if (!string.Equals(mode, "remote", StringComparison.OrdinalIgnoreCase))
        {
            return BrokerDiscoveryOptions.Local();
        }

        var sharedRoot = Environment.GetEnvironmentVariable("MCPBRIDGE_SHARED_ROOT");
        if (string.IsNullOrWhiteSpace(sharedRoot))
        {
            return BrokerDiscoveryOptions.Local();
        }

        var fallbackHost = Environment.GetEnvironmentVariable("MCPBRIDGE_FALLBACK_HOST");
        var fallbackPortText = Environment.GetEnvironmentVariable("MCPBRIDGE_FALLBACK_PORT");
        int? fallbackPort = int.TryParse(fallbackPortText, out var parsedPort) ? parsedPort : null;

        try
        {
            return BrokerDiscoveryOptions.Remote(sharedRoot, fallbackHost, fallbackPort);
        }
        catch (ArgumentException)
        {
            return BrokerDiscoveryOptions.Local();
        }
    }
}
