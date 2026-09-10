using System.Collections.Generic;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Execution;

/// <summary>
/// PRD §07 v2: the auto-dismiss ALLOWLIST for raw Win32 (#32770) dialogs that
/// <see cref="MCPBridge.Core.Diagnostics.DiagnosticSource.Dialogs"/>'s framework suppressor cannot see
/// (DialogSuppressionHandler only handles Revit-framework TaskDialog/MessageBox events, never raw
/// #32770 windows). This is deliberately an ALLOWLIST, never a "close any modal" heuristic: only a
/// window whose class+title exactly matches a known entry is dismissed, and only by the entry's own
/// declared, non-mutating action.
///
/// Each entry carries HOW to dismiss it, because one size does not fit all (issue #129): an informational
/// warning is safe to WM_CLOSE, but a dialog whose other buttons change persistent Revit settings must be
/// dismissed only by clicking its single non-mutating button. §07 is explicit that misfiring on a
/// legitimate window is worse than leaving it to the v1 diagnostic, so the action is per-signature and the
/// adapter's ClickButton is safe-failing (see <see cref="DialogDismissKind"/>).
///
/// Kept here in MCPBridge.Core -- not in the RevitAdapter P/Invoke class -- so the DECISION is a pure,
/// unit-testable function; the untestable window action stays in Win32WindowInventory, which consults this
/// through an injected delegate (no Core -> RevitAdapter dependency inversion needed: Core already
/// references RevitAdapter, so it can name <see cref="DialogDismissAction"/>). This is a pure function,
/// never a script-reachable capability.
/// </summary>
internal static class DialogAutoDismissPolicy
{
    // The one and only allowlist. Win32 class names and titles are matched EXACTLY and case-sensitively
    // (they are exact OS strings, not user-facing localizable prose in the parts we key on). Add future
    // entries here -- this collection is the single source of truth for the auto-dismiss signature AND its
    // per-signature action.
    private static readonly IReadOnlyList<(string ClassName, string Title, DialogDismissAction Action)> Allowlist = new[]
    {
        // The Windows low-memory warning Revit surfaces mid-run (issue #134, live-verified and shipped in
        // #140). Purely informational -- WM_CLOSE dismisses it, mutating no persistent setting.
        ("#32770", "Virtual Memory - High Usage", new DialogDismissAction(DialogDismissKind.PostClose)),

        // "Project Not Saved Recently" (issue #129): a timer-driven reminder that can fire at any moment,
        // including DURING an in-flight Document.Close, and wedges the instance until dismissed. Its four
        // buttons are "Save the project" / "Save the project and set reminder intervals" / "Do not save and
        // set reminder intervals" / "Cancel" -- THREE of which mutate persistent state (a save, or a changed
        // reminder interval). Only Cancel is non-mutating, so this must NOT be a blanket WM_CLOSE (which
        // could resolve to a default that saves or re-times the reminder). Click Cancel by name; if that
        // button can't be found, the adapter does nothing and leaves it to the v1 window-inventory diagnostic.
        ("#32770", "Project Not Saved Recently", new DialogDismissAction(DialogDismissKind.ClickButton, "Cancel")),

        // NOT an allowlist entry, deliberately (issue #129): the Autodesk trial banner ("NN DAYS LEFT")
        // looks like the two above but does NOT wedge the instance -- execute_script, list_instances and a
        // full tier-2 suite were all verified (twice) to run straight through it. Auto-dismissing a
        // licensing surface would be user-hostile and pointless; it is left alone on purpose. This comment
        // is its explicit non-entry so a future reader does not "fix" the omission (see caveats.md).
    };

    /// <summary>
    /// Returns the dismiss action for an exact (<paramref name="className"/>, <paramref name="title"/>)
    /// allowlist match, or null when nothing matches (the window is inventoried as present, not dismissed).
    /// Pure and side-effect free -- the actual WM_CLOSE / button click happens in the adapter.
    /// </summary>
    public static DialogDismissAction? Resolve(string className, string title)
    {
        foreach (var entry in Allowlist)
        {
            if (entry.ClassName == className && entry.Title == title)
            {
                return entry.Action;
            }
        }

        return null;
    }
}
