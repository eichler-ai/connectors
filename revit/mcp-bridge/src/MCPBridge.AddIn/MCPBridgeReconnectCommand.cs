using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

namespace MCPBridge.AddIn;

/// <summary>
/// The "MCP Bridge" ribbon panel's "Reconnect" button (issue #185): asks <see cref="BridgeHost"/> to
/// drop the current connection and re-run broker discovery immediately -- the user-facing "reconnect
/// now" for a broker that was restarted or re-elected while Revit stayed up, which the retry loop's
/// backoff would otherwise pick up only eventually. Stateless; its own top-level class for the same
/// reflection-by-name reason as <see cref="MCPBridgeStatusCommand"/>.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class MCPBridgeReconnectCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
    {
        var host = MCPBridgeApplication.CurrentHost;
        var ownerHandle = commandData.Application.MainWindowHandle;

        if (host is null)
        {
            MCPBridgeStatusWindow.ShowOrActivate(ownerHandle, MCPBridgeStatusCommand.BuildStatusContent(host));
            return Result.Succeeded;
        }

        host.Reconnect("ribbon Reconnect button");

        // The reconnect is asynchronous (it happens on the connection thread), so the connection line
        // here still shows the OLD state -- say so, rather than let "Connected since 20:54" read as
        // "nothing happened".
        MCPBridgeStatusWindow.ShowOrActivate(
            ownerHandle,
            MCPBridgeStatusCommand.BuildStatusContent(host) +
            "\n\nReconnecting to the MCP Server now. Click Status in a few seconds to see the new connection.");

        return Result.Succeeded;
    }
}
