using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace MCPBridge.AddIn;

/// <summary>
/// The "MCP Bridge" ribbon button's status display -- a plain, non-modal WPF window (.Show(), never
/// .ShowDialog()) built entirely in code (no XAML file; content is small and fixed enough that a
/// code-behind-only window is simpler than adding a XAML resource to this project for one screen).
///
/// Deliberately NOT a TaskDialog (an earlier version used one): TaskDialog.Show()/Window.ShowDialog() are
/// both modal -- they block Revit's UI thread until dismissed. Since a script run via execute_script also
/// depends on that same UI thread (through the ExternalEvent bridge BridgeHost owns), a modal here would
/// mean a script that opens it has no way to close it again short of the user clicking it manually --
/// exactly the deadlock risk flagged live when this was being tested via UIApplication.PostCommand. A
/// non-modal window has no such problem: ShowOrActivate/CloseIfOpen below are safe to call from a script
/// (already running on the UI thread) without ever blocking it.
///
/// Singleton-by-design: at most one status window is ever open at a time. Clicking the ribbon button (or
/// re-posting the command) while one is already open just brings it to front rather than stacking
/// duplicates.
/// </summary>
internal static class MCPBridgeStatusWindow
{
    private static Window? _window;

    /// <summary>True while the status window is currently open.</summary>
    public static bool IsOpen => _window is not null;

    public static void ShowOrActivate(IntPtr ownerHandle, string content)
    {
        // Independent PR review finding: this branch used to just Activate() the existing window without
        // ever updating its content, so a window left open across a reconnect/disconnect kept showing
        // whatever status was true at the moment it was first opened -- silently stale for exactly the
        // information (connection status) this button exists to keep honest. Re-clicking now always
        // refreshes the visible content, whether or not a window was already open.
        if (_window is { } existing)
        {
            if (existing.Content is TextBox existingTextBox)
            {
                existingTextBox.Text = content;
            }

            existing.Activate();
            return;
        }

        var window = new Window
        {
            // Unlike TaskDialog.Show(), Revit does NOT auto-prefix a plain WPF Window's title with this
            // add-in's registered <Name> -- that auto-prefixing was specifically a TaskDialog behavior
            // (see the git history on this file/MCPBridgeStatusCommand.cs for the two rounds of live
            // title-wording feedback that established "MCP Bridge - Status" as the correct rendered
            // title). Set the full string directly here since nothing else will add the prefix now.
            Title = "MCP Bridge - Status",
            Width = 420,
            Height = 260,
            ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox
            {
                Text = content,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            },
        };

        // Associates this WPF window with Revit's own main window (a plain Win32 HWND, not WPF) so it
        // behaves like a normal owned window -- minimizes/restores with Revit, sensible z-order/Alt-Tab
        // behavior -- rather than floating as a fully independent top-level window with no relationship
        // to the host application.
        if (ownerHandle != IntPtr.Zero)
        {
            new WindowInteropHelper(window).Owner = ownerHandle;
        }

        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
    }

    /// <summary>No-op if nothing is open. Exists specifically so a script (via execute_script) can close
    /// what it opened without needing a user click -- see this class's own doc comment.</summary>
    public static void CloseIfOpen() => _window?.Close();
}
