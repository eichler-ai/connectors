using Rhino.Commands;

namespace Rhino.MCPBridge.PlugIn;

/// <summary>PRD §04: the one piece of user-facing UI. Phase 1 PR 1 prints to the command line; the
/// Eto panel comes with the update flow.</summary>
public sealed class MCPBridgeStatusCommand : Command
{
    public override string EnglishName => "MCPBridgeStatus";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var host = RhinoMCPBridgePlugIn.CurrentHost;
        if (host is null)
        {
            RhinoApp.WriteLine("MCP Bridge: not running (see startup-errors.log under the connector's app-data folder).");
            return Result.Failure;
        }

        RhinoApp.WriteLine($"MCP Bridge {RhinoMCPBridgePlugIn.BridgeVersion}: listening on 127.0.0.1:{host.Port}, {host.ConnectionCount} MCP Server connection(s).");
        RhinoApp.WriteLine($"  instance {RhinoMCPBridgePlugIn.InstanceId}");
        RhinoApp.WriteLine($"  instance file {host.InstanceFilePath}");
        return Result.Success;
    }
}
