using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using MCPBridge.Core.Connection;
// Autodesk.Revit.UI has its own (ribbon) TextBox; the WPF one is what the prompt below builds.
using TextBox = System.Windows.Controls.TextBox;

namespace MCPBridge.AddIn;

/// <summary>
/// The "MCP Bridge" ribbon panel's "Broker: Local / REMOTE" toggle (issue #185). Flips the running
/// <see cref="BridgeHost"/> between the LOCAL broker (this machine's
/// <c>%LOCALAPPDATA%\Connectors\Revit\broker.json</c>, PRD §05's real target deployment) and a
/// REMOTE one (a shared drive's <c>\\host\share\Connectors\Revit\broker.json</c> -- this project's own
/// Mac+Parallels dev topology) without a Revit restart, and persists the choice to
/// <c>bridge-config.json</c> so it survives restarts AND overrides any leftover
/// <c>MCPBRIDGE_BROKER_MODE</c> in the environment (see <see cref="BrokerModeResolver"/> for why the
/// config outranks the env var).
///
/// <para>Switching TO remote needs a UNC shared root. The prompt is pre-filled from, in order, the
/// config's remembered root, <c>MCPBRIDGE_SHARED_ROOT</c>, and finally this project's own share
/// alias -- so the common dev case is one click plus OK, while nothing is ever written that the user
/// did not see.</para>
///
/// <para>Stateless; its own top-level class for the same reflection-by-name reason as
/// <see cref="MCPBridgeStatusCommand"/>.</para>
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class MCPBridgeBrokerModeCommand : IExternalCommand
{
    /// <summary>Last-resort prompt default when neither config nor environment remembers a root: the
    /// Parallels share alias this repo's own dev tooling uses (dev-environment.md). Only ever a
    /// pre-filled suggestion the user confirms or edits, never applied silently.</summary>
    private const string FallbackSharedRootSuggestion = @"\\Mac\connectors";

    public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
    {
        var host = MCPBridgeApplication.CurrentHost;
        var ownerHandle = commandData.Application.MainWindowHandle;

        if (host is null)
        {
            MCPBridgeStatusWindow.ShowOrActivate(ownerHandle, MCPBridgeStatusCommand.BuildStatusContent(host));
            return Result.Succeeded;
        }

        var configPath = BridgeConfig.DefaultPath();
        var config = BridgeConfig.Load(configPath).Config ?? new BridgeConfig();

        BrokerDiscoveryOptions target;
        if (host.DiscoveryOptions.Mode == BrokerTopologyMode.Remote)
        {
            target = BrokerDiscoveryOptions.Local();
            config.BrokerMode = BridgeConfig.LocalMode;
            // config.SharedRoot deliberately kept: it becomes the next switch-to-remote's default.
        }
        else
        {
            var suggestion = FirstNonBlank(
                config.SharedRoot,
                Environment.GetEnvironmentVariable(BrokerModeResolver.SharedRootVariable),
                FallbackSharedRootSuggestion);

            var sharedRoot = PromptForSharedRoot(ownerHandle, suggestion);
            if (sharedRoot is null)
            {
                return Result.Cancelled; // the user backed out; nothing changed, nothing written.
            }

            try
            {
                target = BrokerDiscoveryOptions.Remote(sharedRoot);
            }
            catch (ArgumentException ex)
            {
                // Same UNC rule the startup path enforces (PRD §09); here it can be explained to the
                // person who typed it rather than logged and silently fallen back from.
                MessageBox.Show(ex.Message, "MCP Bridge - Broker mode", MessageBoxButton.OK, MessageBoxImage.Warning);
                return Result.Cancelled;
            }

            config.BrokerMode = BridgeConfig.RemoteMode;
            config.SharedRoot = sharedRoot;
        }

        // Persist FIRST, then switch: if the write fails the session still switches (the user asked for
        // it, and the running host can do it regardless), but the confirmation says the choice will not
        // survive a restart -- a silent non-persist would recreate #185's "why is it in that mode" hunt.
        string? persistNote = null;
        try
        {
            config.Save(configPath);
        }
        catch (Exception ex)
        {
            persistNote = $"\n\nWARNING: could not write {configPath} ({ex.Message}); this switch applies to the current session only and will NOT survive a Revit restart.";
            MCPBridgeApplication.TryLogDiagnostic($"bridge-config.json save failed after a broker-mode switch: {ex}");
        }

        host.SwitchTo(target);
        MCPBridgeApplication.UpdateBrokerModeButton(target);

        MCPBridgeStatusWindow.ShowOrActivate(
            ownerHandle,
            MCPBridgeStatusCommand.BuildStatusContent(host) +
            $"\n\nSwitched to {MCPBridgeStatusCommand.DescribeMode(target)}. The connection to the previous broker is being dropped and the new one dialed now; " +
            "click Status in a few seconds to see the new connection." +
            (persistNote ?? $"\nSaved to {configPath} (overrides MCPBRIDGE_BROKER_MODE from now on)."));

        return Result.Succeeded;
    }

    /// <summary>
    /// A minimal owned, modal prompt for the shared root -- one label, one text box, OK/Cancel. Modal
    /// is right here: this runs on a real ribbon click by a person, not from a script (the deadlock
    /// hazard MCPBridgeStatusWindow's doc comment describes is specific to a window a SCRIPT might open
    /// and then need to close; nothing scripts can reach opens this one). Returns the trimmed entry, or
    /// null on Cancel/close/blank.
    /// </summary>
    private static string? PromptForSharedRoot(IntPtr ownerHandle, string suggestion)
    {
        var textBox = new TextBox { Text = suggestion, Margin = new Thickness(0, 6, 0, 12), MinWidth = 380 };
        var ok = new Button { Content = "Switch to REMOTE", IsDefault = true, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 4, 12, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Shared drive root for the REMOTE broker, as a UNC path (\\\\host\\share). " +
                   "The add-in will read <root>\\Connectors\\Revit\\broker.json from there instead of this machine's local app-data. " +
                   "In this project's Mac+Parallels dev setup that is the Mac-side broker started by install-mac.sh / redeploy-and-verify.sh.",
        });
        panel.Children.Add(textBox);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = "MCP Bridge - Switch to REMOTE broker",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        if (ownerHandle != IntPtr.Zero)
        {
            new WindowInteropHelper(window).Owner = ownerHandle;
        }

        ok.Click += (_, _) => { window.DialogResult = true; };
        textBox.Loaded += (_, _) => { textBox.Focus(); textBox.SelectAll(); };

        var accepted = window.ShowDialog() == true;
        var entered = textBox.Text?.Trim();
        return accepted && !string.IsNullOrEmpty(entered) ? entered : null;
    }

    private static string FirstNonBlank(string? first, string? second, string fallback)
        => !string.IsNullOrWhiteSpace(first) ? first.Trim()
         : !string.IsNullOrWhiteSpace(second) ? second.Trim()
         : fallback;
}
