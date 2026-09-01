using System.Collections.Generic;
using System.Linq;

namespace MCPBridge.Core.Execution;

/// <summary>
/// PRD §07 v2: the auto-dismiss ALLOWLIST for raw Win32 (#32770) dialogs that
/// <see cref="MCPBridge.Core.Diagnostics.DiagnosticSource.Dialogs"/>'s framework suppressor cannot see
/// (DialogSuppressionHandler only handles Revit-framework TaskDialog/MessageBox events, never raw
/// #32770 windows). This is deliberately an ALLOWLIST, never a "close any modal" heuristic: only a
/// window whose class+title exactly matches a known-benign, informational entry is dismissed, and only
/// by posting WM_CLOSE (no button click, no "do not show again" tick).
///
/// Kept here in MCPBridge.Core -- not in the RevitAdapter P/Invoke class -- so the DECISION is a pure,
/// unit-testable predicate; the untestable window-close action stays in Win32WindowInventory, which
/// consults this predicate through an injected delegate (no Core -> RevitAdapter reference). This is a
/// pure predicate, never a script-reachable capability.
/// </summary>
internal static class DialogAutoDismissPolicy
{
    // The one and only allowlist. Win32 class names and titles are matched EXACTLY and case-sensitively
    // (they are exact OS strings, not user-facing localizable prose in the parts we key on). Add future
    // entries here -- this collection is the single source of truth for the auto-dismiss signature.
    private static readonly IReadOnlyList<(string ClassName, string Title)> Allowlist = new[]
    {
        ("#32770", "Virtual Memory - High Usage"),
    };

    /// <summary>
    /// True iff (<paramref name="className"/>, <paramref name="title"/>) exactly matches an allowlist
    /// entry. Pure and side-effect free -- the actual WM_CLOSE happens in the adapter.
    /// </summary>
    public static bool ShouldDismiss(string className, string title) =>
        Allowlist.Any(entry => entry.ClassName == className && entry.Title == title);
}
