using System.Diagnostics;

namespace Rhino.MCPBridge.PlugIn;

/// <summary>
/// Drives the Go server's client-registration subcommands — <c>mcp-server register</c> /
/// <c>unregister</c> / <c>register --check</c> (PRD §15, phase 7). That engine is the single,
/// cross-platform source of truth for registering the connector with BOTH Claude Code and Claude
/// Desktop, so the plug-in shells it rather than re-implementing registration (and the Claude Desktop
/// MSIX-path handling) in C#. The installer's install.ps1 drives the same subcommands.
/// </summary>
internal static class ServerRegistration
{
    /// <summary>Run <c>&lt;serverExe&gt; &lt;args&gt;</c>, returning the exit code and combined output.
    /// Makes the binary executable first — a yak package built on Windows drops the Mac exec bit.</summary>
    internal static (int exitCode, string output) RunWith(string serverExe, params string[] args)
    {
        EnsureExecutable(serverExe);
        try
        {
            var psi = new ProcessStartInfo(serverExe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return (-1, "could not start " + serverExe);
            // Drain both pipes async with WaitForExit as the one timeout gate (avoids a pipe-buffer deadlock).
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (-1, "the server did not finish within 30 s");
            }
            return (p.ExitCode, (stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()).TrimEnd());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>Locate the packaged server (the <c>RHINO_MCP_SERVER_PATH</c> override, else beside the
    /// plug-in) and run a subcommand; <c>null</c> when no server binary is found (a dev build with no
    /// override).</summary>
    internal static (int exitCode, string output)? Run(params string[] args)
    {
        var server = ServerBinaryLocator.Locate();
        return server is null ? null : RunWith(server, args);
    }

    /// <summary>Add the execute bits on Unix (a yak package built on Windows stores the Mac binary with
    /// none). No-op on Windows; best effort — a still-non-executable binary surfaces when it is run.</summary>
    internal static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch { /* best effort */ }
    }
}
