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
    private static TextBox? _textBox;
    private static Button? _actionButton;

    /// <summary>True while the status window is currently open.</summary>
    public static bool IsOpen => _window is not null;

    /// <summary>
    /// Shows the status window (or refreshes/activates it if already open). <paramref name="actionLabel"/>
    /// and <paramref name="onAction"/> are additive and optional -- pass both non-null to also show a
    /// button below the status text (e.g. "Update Now"); pass null/null (the default) and the window
    /// renders exactly as it always has, a bare read-only text box with no button. This is the important
    /// backward-compatible default: callers that never pass an action see no behavior change.
    /// </summary>
    public static void ShowOrActivate(IntPtr ownerHandle, string content, string? actionLabel = null, Action? onAction = null)
    {
        // Independent PR review finding: this branch used to just Activate() the existing window without
        // ever updating its content, so a window left open across a reconnect/disconnect kept showing
        // whatever status was true at the moment it was first opened -- silently stale for exactly the
        // information (connection status) this button exists to keep honest. Re-clicking now always
        // refreshes the visible content, whether or not a window was already open.
        if (_window is { } existing)
        {
            if (_textBox is not null)
            {
                _textBox.Text = content;
            }

            UpdateActionButton(actionLabel, onAction);
            existing.Activate();
            return;
        }

        // Content is small and fixed enough that a code-behind-only window is simpler than adding a XAML
        // resource to this project for one screen (see class doc comment) -- the button below is added
        // the same way, in code, not by introducing XAML.
        var textBox = new TextBox
        {
            Text = content,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        };

        // DockPanel with LastChildFill (the default): an action button, when present, is inserted BEFORE
        // the text box (see UpdateActionButton) so the text box -- always the last child -- keeps filling
        // the remaining space exactly as it did when it was the window's sole Content.
        var root = new DockPanel();
        root.Children.Add(textBox);

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
            Content = root,
        };

        // Associates this WPF window with Revit's own main window (a plain Win32 HWND, not WPF) so it
        // behaves like a normal owned window -- minimizes/restores with Revit, sensible z-order/Alt-Tab
        // behavior -- rather than floating as a fully independent top-level window with no relationship
        // to the host application.
        if (ownerHandle != IntPtr.Zero)
        {
            new WindowInteropHelper(window).Owner = ownerHandle;
        }

        window.Closed += (_, _) =>
        {
            _window = null;
            _textBox = null;
            _actionButton = null;
        };
        _window = window;
        _textBox = textBox;
        UpdateActionButton(actionLabel, onAction);
        window.Show();
    }

    /// <summary>
    /// Adds, updates, or removes the optional action button below the status text, keyed off whether
    /// both <paramref name="actionLabel"/> and <paramref name="onAction"/> are non-null. Always rebuilds
    /// the button (rather than mutating an existing one) -- simpler than diffing a label change against a
    /// stale click handler closure, and this only runs on a ribbon click, never on a hot path.
    /// </summary>
    private static void UpdateActionButton(string? actionLabel, Action? onAction)
    {
        if (_window?.Content is not DockPanel root)
        {
            return;
        }

        if (_actionButton is not null)
        {
            root.Children.Remove(_actionButton);
            _actionButton = null;
        }

        if (actionLabel is null || onAction is null)
        {
            return;
        }

        var button = new Button
        {
            Content = actionLabel,
            Margin = new Thickness(12, 0, 12, 12),
            Padding = new Thickness(16, 4, 16, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        button.Click += (_, _) => onAction();
        DockPanel.SetDock(button, Dock.Bottom);

        // Inserted before the text box (index 0) so the text box remains the DockPanel's last child and
        // keeps filling the remaining space (LastChildFill) exactly as before this button existed.
        root.Children.Insert(0, button);
        _actionButton = button;
    }

    /// <summary>No-op if nothing is open. Exists specifically so a script (via execute_script) can close
    /// what it opened without needing a user click -- see this class's own doc comment.</summary>
    public static void CloseIfOpen() => _window?.Close();
}
