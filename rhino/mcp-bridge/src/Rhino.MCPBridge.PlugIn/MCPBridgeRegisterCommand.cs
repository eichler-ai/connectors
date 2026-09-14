using System.Diagnostics;
using Rhino.Commands;
using Rhino.MCPBridge.Core.Registration;

namespace Rhino.MCPBridge.PlugIn;

/// <summary>
/// PRD §15 (phase 7): register this connector's MCP server with the user's Claude client. Shells out to
/// the <c>claude</c> CLI (<c>claude mcp add</c>) when it is found, and otherwise prints the config
/// snippet to paste. The user runs this once after installing — <c>MCPBridgeStatus</c> says whether it
/// is needed — and it is never run automatically, so the connector never edits the user's Claude config
/// without being asked (the registration-UX decision for phase 7).
/// </summary>
public sealed class MCPBridgeRegisterCommand : Command
{
    public override string EnglishName => "MCPBridgeRegister";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var server = ServerBinaryLocator.Locate();
        if (server is null)
        {
            RhinoApp.WriteLine($"MCP Bridge: could not find the server binary beside the plug-in. "
                + $"If this is a dev build, set {ServerBinaryLocator.OverrideEnvVar} to a built server; otherwise reinstall the yak package.");
            return Result.Failure;
        }

        var claude = ClaudeCliLocator.Locate();
        if (claude is null)
        {
            RhinoApp.WriteLine("MCP Bridge: the `claude` CLI was not found on PATH or in the usual locations. "
                + "Add this to your Claude client's MCP config manually:");
            RhinoApp.WriteLine(ClientRegistration.SnippetJson(server));
            RhinoApp.WriteLine($"(Claude Code writes user-scope servers to {ClientRegistration.UserConfigPath()}.)");
            return Result.Success;
        }

        // Remove-then-add so re-registering a moved path replaces the old entry rather than clashing on the name.
        RunClaude(claude, ClientRegistration.RemoveArgv());
        var (ok, output) = RunClaude(claude, ClientRegistration.AddArgv(server));
        if (ok)
        {
            RhinoApp.WriteLine($"MCP Bridge: registered with Claude as \"{ClientRegistration.ServerName}\" -> {server}.");
            RhinoApp.WriteLine("Reconnect the server in your client (Claude Code: /mcp) or restart the client to pick it up.");
            return Result.Success;
        }

        RhinoApp.WriteLine("MCP Bridge: `claude mcp add` failed:");
        if (!string.IsNullOrWhiteSpace(output)) RhinoApp.WriteLine(output);
        RhinoApp.WriteLine("Add this to your Claude config manually instead:");
        RhinoApp.WriteLine(ClientRegistration.SnippetJson(server));
        return Result.Failure;
    }

    private static (bool ok, string output) RunClaude(string exe, IReadOnlyList<string> argv)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in argv) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return (false, "could not start " + exe);
            // Drain both pipes asynchronously and let WaitForExit be the one timeout gate: reading a pipe
            // to end synchronously has no timeout and can deadlock if the child fills the other pipe.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (false, "`claude` did not finish within 30 s");
            }
            var output = (stdoutTask.GetAwaiter().GetResult() + stderrTask.GetAwaiter().GetResult()).Trim();
            return (p.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
