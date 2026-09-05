using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;

namespace MCPBridge.AddIn;

/// <summary>
/// The "Update Now" ribbon-button trigger (PRD §12 Stage 3): locates the installed self-copy of
/// <c>install.ps1</c> (written there by any prior install/update, per install.ps1's own
/// <c>Get-AppDir</c>/<c>$selfCopyPath</c> logic) and re-invokes it with <c>-Update -Silent</c> --
/// exactly the shape install.ps1's own comments already anticipate as "the ribbon's -Update -Silent
/// self-update path". No new orchestration logic here; install.ps1 already owns the actual update
/// mechanics (the versioned add-in folders and the current.json pointer flip under the shim, and the
/// stage-and-swap of the broker binary). Nothing is ever asked to close for an update.
///
/// CRITICAL BOUNDARY: this type is <c>internal</c> and its only caller is
/// <see cref="MCPBridgeStatusCommand"/>'s <c>IExternalCommand.Execute()</c> -- a ribbon-button click.
/// It must never become reachable from <c>Connector</c>/<c>ScriptGlobals</c>/anything in the
/// script-execution surface. It lives in MCPBridge.AddIn, which is not one of the three assemblies
/// the "public means script-reachable" rule governs (MCPBridge.Core, MCPBridge.RevitAdapter,
/// Eichler.Connectors.Revit), and is marked internal regardless as defense in depth, since
/// RoslynScriptRunner.LoadableReferences() enumerates every assembly already loaded into the Revit
/// AppDomain -- MCPBridge.AddIn included -- so an internal (not public) member here is the only thing
/// that keeps a hypothetical future script from resolving it even if this assembly is on Roslyn's
/// reference list. Confirmed by reading LoadableReferences(): it does not name MCPBridge.AddIn
/// explicitly, but it does scan all loaded assemblies, so relying on "not explicitly added" alone
/// would not be a real guarantee.
/// </summary>
internal static class UpdateTrigger
{
    /// <summary>
    /// Locates the installed <c>install.ps1</c> self-copy and starts it with
    /// <c>-Update -Silent -Scope &lt;User|AllUsers&gt;</c>, fire-and-forget (never awaited/waited-on).
    /// On success, updates the status window's text via <paramref name="onStarted"/> so stale
    /// pre-update content doesn't linger. If the candidate path doesn't exist, shows a MessageBox
    /// explaining that rather than silently doing nothing or throwing unhandled into Revit's UI
    /// thread.
    ///
    /// <paramref name="ownerHandle"/> is Revit's main window handle (from
    /// <c>ExternalCommandData.Application.MainWindowHandle</c>, threaded through by
    /// <see cref="MCPBridgeStatusCommand"/>) -- both error MessageBoxes below are given this as an
    /// owner so they can never render behind Revit's main window, which would otherwise make Revit
    /// look hung with no visible dialog (exactly the failure mode <see cref="MCPBridgeStatusWindow"/>'s
    /// own class doc comment describes going non-modal to avoid).
    /// </summary>
    /// <summary>
    /// Where this add-in was installed from, derived from where this DLL was loaded. Independent
    /// review finding: picking whichever of the User/AllUsers install.ps1 paths happens to exist
    /// first (existence-based inference) can invoke a stale copy from an old install scope no longer
    /// in use, on a machine that has both. Scope is instead determined DETERMINISTICALLY from which
    /// Addins folder actually loaded this running DLL -- there is exactly one true answer to "which
    /// scope is this session" and it's encoded in our own load path, not in which files happen to be
    /// present on disk. Matches install.ps1's own Get-AddinsDir / Get-AppDir exactly:
    ///   User scope:     %AppData%\Autodesk\Revit\Addins\&lt;version&gt;  ->  %LocalAppData%\Programs\MCPBridge
    ///   AllUsers scope: C:\Program Files\Autodesk\Revit\Addins\&lt;version&gt;  ->  C:\Program Files\MCPBridge
    /// Under the shim (self-update-architecture.md §4.1) Revit loads MCPBridge.Shim.dll from the Addins
    /// folder and THIS DLL is LoadFrom'ed out of &lt;app dir&gt;\addin\&lt;version&gt;\&lt;year&gt;\, so the
    /// all-users signal is the all-users app dir itself. The Addins-folder test beside it covers a DLL
    /// loaded straight from Revit's Addins folder -- not an installer layout any more, but still how
    /// dev-tooling's deploy-and-verify.ps1 drops a build onto the VM.
    /// </summary>
    internal static (string Scope, string AppDir) ResolveInstallLocation()
    {
        var executingAssemblyLocation = Assembly.GetExecutingAssembly().Location;
        var scope = executingAssemblyLocation.Contains(
                @"\Program Files\Autodesk\Revit\Addins\", StringComparison.OrdinalIgnoreCase)
            || executingAssemblyLocation.StartsWith(@"C:\Program Files\MCPBridge\", StringComparison.OrdinalIgnoreCase)
            ? "AllUsers"
            : "User";
        var appDir = scope == "AllUsers"
            ? @"C:\Program Files\MCPBridge"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MCPBridge");
        return (scope, appDir);
    }

    /// <summary>
    /// The release tag install.ps1's version marker (installed-version.json, `version`) says is
    /// installed on disk -- null when there is no marker or it cannot be read. This is what tells the
    /// status window "the update is already installed, only the MCP client needs restarting" apart
    /// from "an update is available": the running server's self-reported version cannot, because a
    /// client keeps running the previous image until it restarts.
    /// </summary>
    internal static string? TryReadInstalledVersion()
    {
        try
        {
            var markerPath = Path.Combine(ResolveInstallLocation().AppDir, "installed-version.json");
            if (!File.Exists(markerPath))
            {
                return null;
            }

            return ReadVersionProperty(markerPath);
        }
        catch
        {
            return null; // best-effort: a missing/corrupt marker only means "cannot tell", never a failed click.
        }
    }

    /// <summary>
    /// The version the shim's pointer (<c>&lt;app dir&gt;\addin\current.json</c>, self-update-architecture.md
    /// §4.1) names -- what a Revit started NOW would load -- or null when it cannot be read (or is
    /// absent, as under a dev-tooling deploy). Distinct from <see cref="TryReadInstalledVersion"/>: the
    /// marker describes the whole install, the pointer is specifically the add-in's "apply" step, and
    /// the Status window compares it with the version THIS process loaded to tell the user whether a
    /// restart is what stands between them and the installed add-in (issue #209). Same BOM tolerance
    /// as the shim's own reader: File.ReadAllText strips the UTF-8 BOM Windows PowerShell writes.
    /// </summary>
    internal static string? TryReadAddinPointerVersion()
    {
        try
        {
            var pointerPath = Path.Combine(ResolveInstallLocation().AppDir, "addin", "current.json");
            return ReadVersionProperty(pointerPath);
        }
        catch
        {
            return null; // best-effort, as above: never a failed Status click.
        }
    }

    private static string? ReadVersionProperty(string jsonPath)
    {
        if (!File.Exists(jsonPath))
        {
            return null;
        }

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
        return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString()
            : null;
    }

    public static void TriggerUpdate(IntPtr ownerHandle, string targetVersionTag, Action<string> onStarted)
    {
        var (scope, appDir) = ResolveInstallLocation();
        // install.ps1's own $selfCopyPath is Join-Path $appDir 'install.ps1'.
        var installScriptPath = Path.Combine(appDir, "install.ps1");

        if (!File.Exists(installScriptPath))
        {
            ShowOwnedMessageBox(
                ownerHandle,
                $"Could not find an installed copy of install.ps1 at:\n{installScriptPath}\n\nUpdate Now requires MCP Bridge to have been installed via install.ps1.",
                "MCP Bridge - Update Now",
                MessageBoxImage.Warning);
            return;
        }

        // The user's own request after the first live Update Now: say what is about to happen to
        // their open Revit windows BEFORE anything happens, and let them back out.
        // Presentation (the user's feedback on the first version, a paragraph of caveats ending in
        // "Yes/No"): the question names the version, the consequences are a short list, and the
        // buttons are OK/Cancel with Cancel as the default so Enter backs out.
        //
        // One truthful text (self-update-architecture.md §6.2): an add-in update writes a new version
        // folder and flips addin\current.json; nothing is asked to close, this Revit keeps the add-in it
        // has until the user restarts it. "Apply" and "load" are two user-controlled steps, and the
        // text says so.
        var proceed = ShowOwnedMessageBox(
            ownerHandle,
            $"Update Revit MCP Bridge to {targetVersionTag}?\n\n" +
            "The update is installed in the background; nothing is closed:\n" +
            "  •  Revit keeps running the add-in it has now.\n" +
            "  •  Restart Revit when convenient to load the new add-in.\n" +
            "  •  If the MCP Server changed too, reconnect the revit server in your MCP client (or restart it).\n" +
            "  •  This window shows what is installed and what is running until then.",
            $"MCP Bridge - Update to {targetVersionTag}",
            MessageBoxImage.Question,
            MessageBoxButton.OKCancel,
            MessageBoxResult.Cancel);
        if (proceed != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            // ProcessStartInfo.ArgumentList (not a single concatenated string) avoids manual-quoting
            // bugs -- install.ps1's own path can legitimately contain spaces (e.g. "Program Files").
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,

                // A -Silent operation triggered by a ribbon click must not pop a visible console window.
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(installScriptPath);
            startInfo.ArgumentList.Add("-Update");
            startInfo.ArgumentList.Add("-Silent");
            startInfo.ArgumentList.Add("-Scope");
            startInfo.ArgumentList.Add(scope);

            Process.Start(startInfo); // fire-and-forget: never waited on.

            // §6.2: the installer stages the new add-in beside the running one and flips the pointer;
            // this Revit is not asked to close and loads the new add-in at its next start. The MCP
            // Server half: since the release manifest the installer skips every component whose hash
            // is unchanged, a changed server image is staged beside the running one, and the running
            // process steps aside on its own (issue #201).
            onStarted(
                "Update started. Nothing is closed: the new add-in is installed beside the running one and Revit keeps the current add-in until you restart it. " +
                "Restart Revit when convenient to load the new add-in -- reopen this window afterwards and the add-in line shows one version again. " +
                "If the MCP Server changed too, the running MCP Server steps aside on its own within about a minute " +
                "and your MCP client's next call starts the new one (if not, reconnect the revit MCP server, e.g. /mcp in Claude Code).");
        }
        catch (Exception ex)
        {
            ShowOwnedMessageBox(
                ownerHandle,
                $"Failed to start the update: {ex.Message}",
                "MCP Bridge - Update Now",
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Shows a modal <see cref="MessageBox"/> owned by Revit's main window, via the same
    /// HWND-to-WPF-window technique <see cref="MCPBridgeStatusWindow"/> already uses
    /// (<c>WindowInteropHelper</c> against a real <c>IntPtr</c> handle) -- an unowned
    /// <c>MessageBox.Show</c> can render behind Revit's main window, making Revit look hung with no
    /// visible dialog. The invisible owner window is a throwaway: sized to nothing, never shown in
    /// the taskbar, never activated on its own, created solely to give the MessageBox a real owner
    /// and closed immediately after.
    /// </summary>
    internal static MessageBoxResult ShowOwnedMessageBox(IntPtr ownerHandle, string text, string caption, MessageBoxImage icon, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        if (ownerHandle == IntPtr.Zero)
        {
            return MessageBox.Show(text, caption, buttons, icon, defaultResult);
        }

        var owner = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Width = 0,
            Height = 0,
            ShowActivated = false,
        };
        new WindowInteropHelper(owner).Owner = ownerHandle;
        owner.Show();
        try
        {
            return MessageBox.Show(owner, text, caption, buttons, icon, defaultResult);
        }
        finally
        {
            owner.Close();
        }
    }
}
