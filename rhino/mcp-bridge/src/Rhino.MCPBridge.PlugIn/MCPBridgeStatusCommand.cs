using Rhino.Commands;
using Rhino.MCPBridge.Core.Registration;

namespace Rhino.MCPBridge.PlugIn;

/// <summary>PRD §04: the one piece of user-facing UI. Phase 1 PR 1 prints to the command line; the
/// Eto panel comes with the update flow. Phase 7 adds the Claude-registration state, so the user can see
/// whether <c>MCPBridgeRegister</c> is still owed.</summary>
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
        WriteRegistrationLine();
        return Result.Success;
    }

    /// <summary>Report where the connector stands with the user's Claude registration, and point at
    /// <c>MCPBridgeRegister</c> when a (re-)register is owed (PRD §15).</summary>
    private static void WriteRegistrationLine()
    {
        var server = ServerBinaryLocator.Locate();
        if (server is null)
        {
            RhinoApp.WriteLine($"  Claude registration: server binary not found beside the plug-in (dev build? set {ServerBinaryLocator.OverrideEnvVar}).");
            return;
        }

        var config = SafeRead(ClientRegistration.UserConfigPath());
        switch (ClientRegistration.StateFor(config, server))
        {
            case RegistrationState.Current:
                RhinoApp.WriteLine($"  Claude registration: registered ({ClientRegistration.ServerName} -> {server}).");
                break;
            case RegistrationState.StalePath:
                RhinoApp.WriteLine("  Claude registration: registered at a different path — run MCPBridgeRegister to update it.");
                break;
            case RegistrationState.Unregistered:
                RhinoApp.WriteLine("  Claude registration: not registered — run MCPBridgeRegister.");
                break;
        }
    }

    private static string? SafeRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }
}
