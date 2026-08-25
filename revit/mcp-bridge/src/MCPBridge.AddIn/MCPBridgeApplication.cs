using System;
using System.IO;
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
        try
        {
            // Second independent PR review finding: this used to sit outside the try block below, so a
            // throwing Register() (e.g. a future change to it that isn't as defensive as its current
            // implementation) would propagate out of OnStartup completely unlogged -- the exact silent-
            // failure symptom TryLogStartupFailure exists to close off, just for a different call.
            AssemblyResolution.Register();

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
        catch (Exception ex)
        {
            // A failed OnStartup must not take down all of Revit -- report failure to the AddInLoader
            // instead of letting the exception propagate (PRD §04). Independent PR review finding: this
            // used to swallow the exception with zero trace anywhere, reproducing the exact silent-no-load
            // symptom the "verifying you're actually debugging the binary you just built" skill-file
            // section (added this same session) exists to make fast to rule out -- a genuinely-thrown
            // OnStartup exception and a manifest Revit never loaded at all were otherwise indistinguishable
            // from the outside. Best-effort only (never let a logging failure mask the real one); path is
            // computed per-machine, not hardcoded to any one developer's username.
            TryLogStartupFailure(ex);
            return Result.Failed;
        }
    }

    private static void TryLogStartupFailure(Exception ex)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCPBridge");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "startup-errors.log"), $"{DateTimeOffset.UtcNow:O} OnStartup failed: {ex}\n");
        }
        catch
        {
            // Best-effort diagnostic only -- a failure here must never mask or replace the original
            // exception, which is already being reported to the AddInLoader via Result.Failed.
        }
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
