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
/// mechanics (per-version deploy-skip-if-running, the deferred-update Scheduled Task, re-updating the
/// broker binary).
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
    public static void TriggerUpdate(IntPtr ownerHandle, Action<string> onStarted)
    {
        // Independent review finding: picking whichever of the User/AllUsers install.ps1 paths
        // happens to exist first (existence-based inference) can invoke a stale copy from an old
        // install scope no longer in use, on a machine that has both. Scope is instead determined
        // DETERMINISTICALLY from which Addins folder actually loaded this running DLL -- there is
        // exactly one true answer to "which scope is this session" and it's encoded in our own
        // load path, not in which files happen to be present on disk. Matches install.ps1's own
        // Get-AddinsDir exactly (install.ps1 ~line 75-83):
        //   User scope:     %AppData%\Autodesk\Revit\Addins\<version>
        //   AllUsers scope: C:\Program Files\Autodesk\Revit\Addins\<version>
        var executingAssemblyLocation = Assembly.GetExecutingAssembly().Location;
        var scope = executingAssemblyLocation.Contains(
            @"\Program Files\Autodesk\Revit\Addins\", StringComparison.OrdinalIgnoreCase)
            ? "AllUsers"
            : "User";

        // Matches install.ps1's own Get-AppDir exactly (install.ps1 ~line 85):
        //   User scope:     %LocalAppData%\Programs\MCPBridge
        //   AllUsers scope: C:\Program Files\MCPBridge
        // install.ps1's own $selfCopyPath is Join-Path $appDir 'install.ps1'.
        var installScriptPath = scope == "AllUsers"
            ? Path.Combine(@"C:\Program Files\MCPBridge", "install.ps1")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MCPBridge", "install.ps1");

        if (!File.Exists(installScriptPath))
        {
            ShowOwnedMessageBox(
                ownerHandle,
                $"Could not find an installed copy of install.ps1 at:\n{installScriptPath}\n\nUpdate Now requires MCP Bridge to have been installed via install.ps1.",
                "MCP Bridge - Update Now",
                MessageBoxImage.Warning);
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

                // Independent review finding: install.ps1 itself uses -WindowStyle Hidden for its own
                // background watcher (install.ps1 ~line 171) for the identical reason -- a -Silent
                // operation triggered by a ribbon click must not pop a visible console window.
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

            // Independent review finding: install.ps1's -Silent flag SKIPS Revit's automatic relaunch
            // (install.ps1 ~line 564: `if (-not $Silent -and ...)`) -- under -Silent, Revit closes and
            // does NOT reopen on its own, so the previous "may close and reopen shortly" wording was
            // simply wrong about what happens next.
            onStarted("Update started. Revit will close shortly to apply it; reopen it manually once the update finishes.");
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
    private static void ShowOwnedMessageBox(IntPtr ownerHandle, string text, string caption, MessageBoxImage icon)
    {
        if (ownerHandle == IntPtr.Zero)
        {
            MessageBox.Show(text, caption, MessageBoxButton.OK, icon);
            return;
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
            MessageBox.Show(owner, text, caption, MessageBoxButton.OK, icon);
        }
        finally
        {
            owner.Close();
        }
    }
}
