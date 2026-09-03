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
        var ownerHandle = commandData.Application.MainWindowHandle;

        // Which component is behind? The comparison above is server-vs-latest only, and after the
        // first live update (v0.1.1 -> v0.1.2) that produced "Update available (v0.1.2)" on a Revit
        // whose add-in WAS v0.1.2, because the MCP client was still running the previous server image.
        // The installer's own version marker says what is installed on disk; when it already matches
        // the latest release, the only step left is restarting the MCP client, so say exactly that and
        // offer no button -- re-running the installer cannot restart another process's server.
        if (updateAvailable)
        {
            var latestTag = UpdateAvailability.DisplayTag(latestAvailableVersion!);
            var runningTag = UpdateAvailability.DisplayTag(host!.BrokerVersion!);
            var installedTag = UpdateTrigger.TryReadInstalledVersion();
            if (installedTag is not null && string.Equals(UpdateAvailability.DisplayTag(installedTag), latestTag, StringComparison.OrdinalIgnoreCase))
            {
                // "Quit fully": found live -- closing Claude Desktop's window left its server process
                // (and therefore the old image) running; only a real quit, or reconnecting the revit
                // server inside the client, starts the new exe.
                content +=
                    $"\n\n{latestTag} is installed. The MCP Server you are connected to is still {runningTag}. " +
                    "To load the new one, reconnect the revit server in your MCP client, or quit the client fully " +
                    "(closing its window may leave the old server running) and start it again.";
                MCPBridgeStatusWindow.ShowOrActivate(ownerHandle, content);
                return Result.Succeeded;
            }

            content += $"\n\nUpdate available: {latestTag} (MCP Server running {runningTag})";
        }

        if (updateAvailable)
        {
            MCPBridgeStatusWindow.ShowOrActivate(
                ownerHandle,
                content,
                actionLabel: "Update Now",
                onAction: () => UpdateTrigger.TriggerUpdate(
                    ownerHandle,
                    UpdateAvailability.DisplayTag(latestAvailableVersion!),
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
            connectionLine = $"Connected to the MCP Server at {host.BrokerAddress} since {since}.";
        }
        else
        {
            connectionLine = "Not connected -- reconnecting in the background.";
        }

        var (buildTimestamp, gitCommit, addInVersion) = ReadBuildIdentity();

        return
            $"Instance ID: {MCPBridgeApplication.InstanceId}\n" +
            $"MCP Server: {DescribeMode(host?.DiscoveryOptions)}\n" +
            $"Status: {connectionLine}\n\n" +
            $"Add-in: {addInVersion} (build {buildTimestamp}, commit {gitCommit})";
    }

    /// <summary>
    /// "Local" or a deliberately loud "REMOTE", each with the broker.json path actually being read --
    /// the one fact that settles "which broker is this Revit registered with" (issue #185's symptom
    /// was a healthy-looking Revit registered with a broker nobody was querying). User-facing text
    /// says "MCP Server", never "broker" (CONVENTIONS.md; the user's own request on #187).
    /// </summary>
    internal static string DescribeMode(BrokerDiscoveryOptions? options)
    {
        if (options is null)
        {
            return "unknown";
        }

        var brokerJson = Path.Combine(options.ConnectorRoot, "broker.json");
        return options.Mode == BrokerTopologyMode.Remote
            ? $"REMOTE, on another machine (found via {brokerJson})"
            : $"Local, on this machine (found via {brokerJson})";
    }

    /// <summary>
    /// Build identity for "am I running the build I think I'm running" -- the same question that took
    /// deliberate diagnostic logging to answer during this add-in's own live-wiring debugging (see the
    /// revit-connector-development skill's "Confirm you are running the artifact you just built"
    /// rule); surfacing it in the UI means answering it never again needs a log file or a
    /// screen-sharing session.
    /// </summary>
    private static (string BuildTimestamp, string GitCommit, string AddInVersion) ReadBuildIdentity()
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
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToList();
        var gitCommit = metadata.FirstOrDefault(a => a.Key == "GitCommit")?.Value;

        // The release tag the pipeline embedded (MCPBridge.AddIn.csproj's MCPBridgeEmbedVersion,
        // from MCPBRIDGE_VERSION); "dev" for a local build. Shown so a person can see at a glance
        // whether the ADD-IN is current, independently of what the MCP Server reports.
        var version = metadata.FirstOrDefault(a => a.Key == "Version")?.Value;
        var addInVersion = string.IsNullOrWhiteSpace(version) || version == "dev" ? "dev build" : UpdateAvailability.DisplayTag(version);

        return (buildTimestamp, string.IsNullOrWhiteSpace(gitCommit) ? "unknown" : gitCommit, addInVersion);
    }
}
