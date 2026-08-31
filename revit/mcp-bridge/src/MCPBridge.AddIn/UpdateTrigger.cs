using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

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
    /// Locates the installed <c>install.ps1</c> self-copy (checking the User scope path, then the
    /// AllUsers scope path -- either order, first found wins) and starts it with
    /// <c>-Update -Silent -Scope &lt;User|AllUsers&gt;</c>, fire-and-forget (never awaited/waited-on).
    /// On success, updates the status window's text via <paramref name="onStarted"/> so stale
    /// pre-update content doesn't linger. If neither candidate path exists, shows a MessageBox
    /// explaining that rather than silently doing nothing or throwing unhandled into Revit's UI
    /// thread.
    /// </summary>
    public static void TriggerUpdate(Action<string> onStarted)
    {
        // Matches install.ps1's own Get-AppDir exactly (install.ps1 ~line 85):
        //   User scope:     %LocalAppData%\Programs\MCPBridge
        //   AllUsers scope: C:\Program Files\MCPBridge
        // install.ps1's own $selfCopyPath is Join-Path $appDir 'install.ps1'.
        var userScopePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MCPBridge", "install.ps1");
        var allUsersScopePath = Path.Combine(@"C:\Program Files\MCPBridge", "install.ps1");

        string installScriptPath;
        string scope;
        if (File.Exists(userScopePath))
        {
            installScriptPath = userScopePath;
            scope = "User";
        }
        else if (File.Exists(allUsersScopePath))
        {
            installScriptPath = allUsersScopePath;
            scope = "AllUsers";
        }
        else
        {
            MessageBox.Show(
                $"Could not find an installed copy of install.ps1 at either:\n{userScopePath}\n{allUsersScopePath}\n\nUpdate Now requires MCP Bridge to have been installed via install.ps1.",
                "MCP Bridge - Update Now",
                MessageBoxButton.OK,
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

            onStarted("Update started -- Revit may close and reopen shortly.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to start the update: {ex.Message}",
                "MCP Bridge - Update Now",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
