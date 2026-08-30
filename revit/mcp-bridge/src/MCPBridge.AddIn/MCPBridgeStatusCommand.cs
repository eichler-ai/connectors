using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

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

        var content =
            $"Instance ID: {MCPBridgeApplication.InstanceId}\n" +
            $"Status: {connectionLine}\n\n" +
            $"Build: {buildTimestamp}\n" +
            $"Commit: {gitCommit}";

        MCPBridgeStatusWindow.ShowOrActivate(commandData.Application.MainWindowHandle, content);

        return Result.Succeeded;
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
