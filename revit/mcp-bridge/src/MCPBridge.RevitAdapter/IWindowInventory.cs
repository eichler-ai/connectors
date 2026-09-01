using System;
using System.Collections.Generic;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Non-framework-dialog fallback (PRD §07). v1 was diagnosis-only; v2 adds a narrow auto-DISMISS action
/// for raw Win32 (#32770) dialogs that the Revit-framework suppressor cannot see. First P/Invoke boundary
/// in this repo -- kept behind this interface, implemented by Win32WindowInventory, so MCPBridge.Core
/// stays testable against a fake, matching the IDocumentAdapter/ITransactionAdapter seam pattern.
/// </summary>
public interface IWindowInventory
{
    /// <summary>
    /// Enumerates this process's owned top-level windows. For each one, <paramref name="shouldDismiss"/>
    /// (className, title) is consulted: a match is dismissed via a fire-and-forget WM_CLOSE and reported
    /// on <see cref="WindowInventorySnapshot.Dismissed"/> INSTEAD of appearing among the present Windows;
    /// a non-match is inventoried exactly as in v1. The predicate lives in MCPBridge.Core
    /// (DialogAutoDismissPolicy) so the allowlist DECISION stays unit-testable while this P/Invoke ACTION
    /// does not depend on Core.
    ///
    /// <paramref name="onDismissed"/> is invoked synchronously the moment each window is dismissed, in
    /// addition to the returned snapshot. This is the caller's side channel for #138: the whole pass runs
    /// under a wire-budget cap that may ABANDON (discard) its return value, but a dismissal is an action
    /// already taken and MUST still be reported (§01) — so the caller captures dismissals through this
    /// callback, which survives the abandonment the return value does not.
    /// </summary>
    WindowInventorySnapshot EnumerateOwnedTopLevelWindows(
        Func<string, string, bool> shouldDismiss,
        Action<DismissedDialog> onDismissed);
}

/// <summary>
/// One enumeration pass's result. Truncated is PRD §01 honesty (independent PR review finding):
/// the real implementation stops enumerating once its overall time budget lapses (see
/// Win32WindowInventory.OverallBudgetMs), and a half-enumerated inventory presented as complete
/// would have an agent -- or a human triaging a stuck dialog -- conclude a window doesn't exist
/// when it merely wasn't reached. When Truncated is true, the §07 fallback notice says so.
/// </summary>
public sealed record WindowInventorySnapshot(
    IReadOnlyList<WindowInfo> Windows,
    bool Truncated,
    IReadOnlyList<DismissedDialog> Dismissed)
{
    /// <summary>Convenience for the no-dismissal case: Dismissed defaults to empty.</summary>
    public WindowInventorySnapshot(IReadOnlyList<WindowInfo> Windows, bool Truncated)
        : this(Windows, Truncated, Array.Empty<DismissedDialog>())
    {
    }
}

/// <summary>
/// A window the §07 v2 allowlist matched and this pass posted WM_CLOSE to (best-effort). Carries only
/// its class+title -- string identifiers, no window handle -- so this public, script-reachable type
/// exposes no capability. Reported as a §01 notice by the caller so an auto-dismiss is never silent.
/// </summary>
public sealed record DismissedDialog(string ClassName, string Title);

/// <summary>
/// ChildText is best-effort: static/label/button control text collected from a window's immediate
/// children (EnumChildWindows + GetWindowText). Empty for owner-drawn/canvas-rendered dialogs (WPF and
/// some custom WinForms controls don't expose text this way) -- Title remains available as a fallback
/// even when child-text extraction comes back empty.
/// </summary>
public sealed record WindowInfo(string Title, string ClassName, IReadOnlyList<string> ChildText);
