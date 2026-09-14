using Rhino.Commands;

namespace Rhino.MCPBridge.PlugIn;

/// <summary>
/// PRD §15 (phase 7): register this connector's MCP server with the user's Claude clients — Claude Code
/// AND Claude Desktop. A thin front end over the server's own <c>register</c> subcommand (the single
/// cross-platform engine, shared with install.ps1); this command just locates the packaged server and
/// runs it, relaying its per-client report. The user runs it once after installing — never automatically,
/// so the connector never edits the user's Claude config unasked (the registration-UX decision).
/// </summary>
public sealed class MCPBridgeRegisterCommand : Command
{
    public override string EnglishName => "MCPBridgeRegister";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var result = ServerRegistration.Run("register");
        if (result is null)
        {
            RhinoApp.WriteLine($"MCP Bridge: could not find the server binary beside the plug-in. "
                + $"If this is a dev build, set {ServerBinaryLocator.OverrideEnvVar} to a built server; otherwise reinstall the yak package.");
            return Result.Failure;
        }

        foreach (var line in result.Value.output.Split('\n'))
        {
            RhinoApp.WriteLine(line.TrimEnd());
        }
        if (result.Value.exitCode == 0)
        {
            RhinoApp.WriteLine("Reconnect the server in your client (Claude Code: /mcp) or restart the client to pick it up.");
            return Result.Success;
        }
        return Result.Failure;
    }
}
