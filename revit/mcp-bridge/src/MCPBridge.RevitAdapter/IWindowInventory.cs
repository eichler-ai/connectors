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
    /// Enumerates this process's owned top-level windows. For each one, <paramref name="resolveDismiss"/>
    /// (className, title) is consulted: a non-null result is a dismiss ACTION and the window is dismissed
    /// (best-effort) and handed to <paramref name="onDismissed"/> INSTEAD of appearing among the snapshot's
    /// present Windows; a null result is inventoried exactly as in v1. The decision lives in MCPBridge.Core
    /// (DialogAutoDismissPolicy) so the allowlist stays unit-testable while this P/Invoke ACTION does not
    /// depend on Core.
    ///
    /// <para>The action is either <see cref="DialogDismissKind.PostClose"/> (fire-and-forget WM_CLOSE) or
    /// <see cref="DialogDismissKind.ClickButton"/> (click one named button, best-effort). ClickButton is
    /// deliberately safe-failing: if the named button can't be found, NOTHING is clicked and the window is
    /// inventoried as present -- a wrong-button click could mutate persistent Revit settings (PRD §07,
    /// "Project Not Saved Recently"), so "leave it for the v1 diagnostic" always beats guessing.</para>
    ///
    /// <paramref name="onDismissed"/> is invoked synchronously the moment each window is dismissed, in
    /// addition to the returned snapshot. This is the caller's side channel for #138: the whole pass runs
    /// under a wire-budget cap that may ABANDON (discard) its return value, but a dismissal is an action
    /// already taken and MUST still be reported (§01) — so the caller captures dismissals through this
    /// callback, which survives the abandonment the return value does not.
    /// </summary>
    WindowInventorySnapshot EnumerateOwnedTopLevelWindows(
        Func<string, string, DialogDismissAction?> resolveDismiss,
        Action<DismissedDialog> onDismissed);
}

/// <summary>
/// How the §07 v2 allowlist says a matched dialog should be dismissed. Defined here in RevitAdapter (the
/// P/Invoke boundary), not in Core, so the Core allowlist policy can RETURN it without a back-reference:
/// Core references RevitAdapter, never the reverse. Capability-free (an enum + a plain string label), so
/// this public, script-reachable type exposes nothing an untrusted script could act on.
/// </summary>
public enum DialogDismissKind
{
    /// <summary>Post WM_CLOSE — closes the dialog as if its title-bar X were clicked, ticking nothing and
    /// clicking no button. For a standard informational #32770 dialog this is the safe, no-op-equivalent
    /// close (e.g. the "Virtual Memory - High Usage" warning).</summary>
    PostClose,

    /// <summary>Click one specific NAMED button (best-effort), and do nothing if it can't be found. Used
    /// when a blanket WM_CLOSE is unsafe because the dialog's OTHER buttons mutate persistent state and
    /// only one named button is non-mutating -- PRD §07's "Project Not Saved Recently", whose three other
    /// buttons save or change the reminder interval, leaving Cancel as the only safe choice.</summary>
    ClickButton,
}

/// <summary>
/// A §07 v2 dismiss action: the <see cref="DialogDismissKind"/> and, for
/// <see cref="DialogDismissKind.ClickButton"/>, the exact button label to click. The label is matched
/// against each candidate button's text with its accelerator ampersand and surrounding whitespace ignored
/// and case-insensitively (button captions are user-facing, unlike the exactly-matched class/title).
/// Capability-free — an enum and a string only.
/// </summary>
public sealed record DialogDismissAction(DialogDismissKind Kind, string? ButtonText = null);

/// <summary>
/// One enumeration pass's result. Truncated is PRD §01 honesty (independent PR review finding):
/// the real implementation stops enumerating once its overall time budget lapses (see
/// Win32WindowInventory.OverallBudgetMs), and a half-enumerated inventory presented as complete
/// would have an agent -- or a human triaging a stuck dialog -- conclude a window doesn't exist
/// when it merely wasn't reached. When Truncated is true, the §07 fallback notice says so.
/// </summary>
public sealed record WindowInventorySnapshot(IReadOnlyList<WindowInfo> Windows, bool Truncated);

/// <summary>
/// A window the §07 v2 allowlist matched and this pass dismissed (best-effort) -- either by posting
/// WM_CLOSE or by clicking a single named non-mutating button. Carries only its class+title -- string
/// identifiers, no window handle -- so this public, script-reachable type exposes no capability. Delivered
/// through EnumerateOwnedTopLevelWindows's onDismissed callback (NOT on the snapshot) so the caller can
/// report it as a §01 notice even when the pass's return value is discarded by the #138 wire-budget cap; a
/// dismissal is an action already taken and must never be silent.
/// </summary>
public sealed record DismissedDialog(string ClassName, string Title);

/// <summary>
/// ChildText is best-effort: static/label/button control text collected from a window's immediate
/// children (EnumChildWindows + GetWindowText). Empty for owner-drawn/canvas-rendered dialogs (WPF and
/// some custom WinForms controls don't expose text this way) -- Title remains available as a fallback
/// even when child-text extraction comes back empty.
/// </summary>
public sealed record WindowInfo(string Title, string ClassName, IReadOnlyList<string> ChildText);
