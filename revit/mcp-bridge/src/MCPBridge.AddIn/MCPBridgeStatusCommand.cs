using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using MCPBridge.Core.Connection;

namespace MCPBridge.AddIn;

/// <summary>
/// The "MCP Bridge" ribbon panel's "Status" button (registered in MCPBridgeApplication.OnStartup). Shows
/// connection status and build identity in MCPBridgeStatusWindow -- no state of its own, reads everything
/// from MCPBridgeApplication.CurrentHost/InstanceId at click time.
///
/// Revit ribbon buttons are invoked via reflection against a named IExternalCommand type (PushButtonData
/// takes a class name string, not a delegate), so this has to be its own top-level class rather than a
/// method on MCPBridgeApplication -- the same reason RevitScriptExecutionHandler/DocumentSnapshotHandler
/// are their own classes rather than inline lambdas elsewhere in this add-in.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class MCPBridgeStatusCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
    {
        var host = MCPBridgeApplication.CurrentHost;

        // Independent review finding: BridgeHost's version fields were previously only ever written
        // once per TCP connection, so a Revit session that stayed connected for days would never see
        // a release published mid-session until the connection happened to drop and reconnect. Force
        // a fresh, connection-independent broker.json re-read on every click, before reading
        // BrokerVersion/LatestAvailableVersion below, so the status window always shows a current
        // comparison rather than a stale connect-time snapshot.
        host?.RefreshVersionStatus();

        // PRD §12 Stage 3: broker.json's own Version/LatestAvailableVersion fields (Stage 1 plumbing,
        // Stage 2's periodic GitHub check) reached here via BridgeHost's existing volatile status
        // fields -- the same reconnect-loop poll path already used for IsConnected/BrokerAddress, no
        // new RPC. UpdateAvailability.IsAvailable is entirely broker-sourced; it deliberately does NOT
        // factor in gitCommit/buildTimestamp (a different, build-identity purpose).
        var latestAvailableVersion = host?.LatestAvailableVersion;
        var updateAvailable = UpdateAvailability.IsAvailable(host?.BrokerVersion, latestAvailableVersion);

        var content = BuildStatusContent(host);
        if (updateAvailable)
        {
            content += $"\n\nUpdate available (v{latestAvailableVersion})";
        }

        var ownerHandle = commandData.Application.MainWindowHandle;

        if (updateAvailable)
        {
            MCPBridgeStatusWindow.ShowOrActivate(
                ownerHandle,
                content,
                actionLabel: "Update Now",
                onAction: () => UpdateTrigger.TriggerUpdate(
                    ownerHandle,
                    statusText => MCPBridgeStatusWindow.ShowOrActivate(ownerHandle, statusText)));
        }
        else
        {
            MCPBridgeStatusWindow.ShowOrActivate(ownerHandle, content);
        }

        return Result.Succeeded;
    }

    /// <summary>
    /// The status text proper -- instance, broker mode, connection state, build identity. Shared with
    /// the Reconnect and Broker-mode commands (issue #185) so every window this panel opens reads the
    /// same way, and so a switch's confirmation shows the mode it just switched to in the same words
    /// Status will use afterwards.
    /// </summary>
    internal static string BuildStatusContent(BridgeHost? host)
    {
        string connectionLine;
        if (host is null)
        {
            connectionLine = "Not started (BridgeHost unavailable).";
        }
        else if (host.IsConnected)
        {
            var since = host.ConnectedSince is { } connectedSince
                ? connectedSince.ToLocalTime().ToString("HH:mm:ss")
                : "unknown time";
            connectionLine = $"Connected to broker at {host.BrokerAddress} since {since}.";
        }
        else
        {
            connectionLine = "Not connected -- reconnecting in the background.";
        }

        var (buildTimestamp, gitCommit) = ReadBuildIdentity();

        return
            $"Instance ID: {MCPBridgeApplication.InstanceId}\n" +
            $"Broker mode: {DescribeMode(host?.DiscoveryOptions)}\n" +
            $"Status: {connectionLine}\n\n" +
            $"Build: {buildTimestamp}\n" +
            $"Commit: {gitCommit}";
    }

    /// <summary>
    /// "Local" or a deliberately loud "REMOTE", each with the broker.json path actually being read --
    /// the one fact that settles "which broker is this Revit registered with" (issue #185's symptom
    /// was a healthy-looking Revit registered with a broker nobody was querying).
    /// </summary>
    internal static string DescribeMode(BrokerDiscoveryOptions? options)
    {
        if (options is null)
        {
            return "unknown";
        }

        var brokerJson = Path.Combine(options.ConnectorRoot, "broker.json");
        return options.Mode == BrokerTopologyMode.Remote
            ? $"REMOTE (shared drive) -- {brokerJson}"
            : $"Local -- {brokerJson}";
    }

    /// <summary>
    /// Build identity for "am I running the build I think I'm running" -- the same question that took
    /// deliberate diagnostic logging to answer during this add-in's own live-wiring debugging (see the
    /// revit-connector-development skill's "Confirm you are running the artifact you just built"
    /// rule); surfacing it in the UI means answering it never again needs a log file or a
    /// screen-sharing session.
    /// </summary>
    private static (string BuildTimestamp, string GitCommit) ReadBuildIdentity()
    {
        var assembly = typeof(MCPBridgeStatusCommand).Assembly;

        string buildTimestamp;
        try
        {
            buildTimestamp = new FileInfo(assembly.Location).LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch
        {
            buildTimestamp = "unknown";
        }

        // Embedded by MCPBridge.AddIn.csproj's MCPBridgeEmbedGitCommit target (git rev-parse --short HEAD
        // at build time); absent (falls through to "unknown") on a machine without git on PATH or building
        // outside a git checkout -- deliberately non-fatal, this is a diagnostic convenience, not a
        // build-correctness requirement.
        var gitCommit = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GitCommit")?.Value;

        return (buildTimestamp, string.IsNullOrWhiteSpace(gitCommit) ? "unknown" : gitCommit);
    }
}
