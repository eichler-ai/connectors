using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Real Win32 EnumWindows-based implementation (PRD §07 v1). Not unit-tested -- see
/// RevitTransactionAdapter's own doc comment for why. Called from RequestDispatcher's own thread (the
/// TCP/background thread), never Revit's UI thread, so it stays reachable even while that thread is
/// blocked behind a modal dialog -- that's the whole point of this fallback. Running off the UI
/// thread is necessary but NOT sufficient for that reachability: reading same-process window TEXT
/// re-introduces the UI-thread dependency through WM_GETTEXT, which is why GetWindowText below is
/// timeout-bounded -- see its comment for the live deadlock the unbounded form caused.
/// </summary>
public sealed class Win32WindowInventory : IWindowInventory
{
    private const int MaxLength = 512;

    // See GetWindowText: WM_GETTEXT via SendMessageTimeout, bounded per window AND by an overall
    // budget for the whole enumeration. Both bounds are load-bearing, learned in two live rounds:
    // the per-window bound alone still effectively deadlocked the caller, because
    // SMTO_ABORTIFHUNG's "hung" only kicks in after the OS's ~5s no-pump threshold -- a UI thread
    // merely BUSY with a fresh long-running script is not yet "hung", so every one of Revit's
    // hundreds of top-level-plus-child windows waited its full per-window timeout serially.
    // The overall budget caps the whole pass regardless; a truncated inventory is an honest
    // degrade for a diagnosis-only feature, and the caller's wire deadline stays comfortably met.
    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_CLOSE = 0x0010;
    // BM_CLICK simulates a click on a button control: it posts the down/up pair and notifies the parent
    // with BN_CLICKED, exactly as a real click would. Posted (async), never sent, for the same UI-thread
    // reason as WM_CLOSE below.
    private const uint BM_CLICK = 0x00F5;
    private const string ButtonClassName = "Button";
    private const uint SMTO_BLOCK = 0x0001;
    private const uint SMTO_ABORTIFHUNG = 0x0008;
    private const uint PerWindowTextTimeoutMs = 100;
    private const long OverallBudgetMs = 2000;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>Placeholder for a title whose WM_GETTEXT read timed out -- reported, not dropped (PRD §01).</summary>
    internal const string TextUnavailablePlaceholder = "<text unavailable within budget>";

    public WindowInventorySnapshot EnumerateOwnedTopLevelWindows(
        Func<string, string, DialogDismissAction?> resolveDismiss,
        Action<DismissedDialog> onDismissed)
    {
        var currentProcessId = (uint)Environment.ProcessId;
        var results = new List<WindowInfo>();
        var budget = System.Diagnostics.Stopwatch.StartNew();
        var truncated = false;

        bool TopLevelCallback(IntPtr hWnd, IntPtr lParam)
        {
            if (budget.ElapsedMilliseconds > OverallBudgetMs)
            {
                truncated = true;
                return false; // stop enumerating -- see OverallBudgetMs
            }

            GetWindowThreadProcessId(hWnd, out var owningProcessId);
            if (owningProcessId != currentProcessId)
            {
                return true;
            }

            var title = GetWindowText(hWnd, out var titleTimedOut);
            if (titleTimedOut)
            {
                title = TextUnavailablePlaceholder;
                truncated = true;
            }

            var className = GetClassNameOf(hWnd);

            // §07 v2: consult the Core-owned allowlist for a per-signature dismiss ACTION. A match is
            // dismissed (fire-and-forget WM_CLOSE, or a click on one named non-mutating button) and handed
            // to onDismissed instead of being listed as a present window -- it is on its way out, so
            // reporting it as still-present would be misleading. onDismissed (not the snapshot) is the
            // reporting channel, so the dismissal survives the caller's #138 wire-budget abandon. Only the
            // plain title is used to match, so a match with a timed-out title never happens (the placeholder
            // never matches the allowlist).
            var dismissAction = resolveDismiss(className, title);
            if (dismissAction is not null)
            {
                if (TryDismiss(hWnd, dismissAction))
                {
                    onDismissed(new DismissedDialog(className, title));
                    return true;
                }

                // Dismiss did not happen (post failed, or a ClickButton whose named button wasn't found):
                // degrade to "not dismissed" and fall through to inventory it as present, so the dialog is
                // still surfaced for manual triage (same best-effort posture as the rest of this class). A
                // ClickButton that can't find its button clicks NOTHING -- never a fallback WM_CLOSE or a
                // guessed button -- because the whole reason it isn't PostClose is that a wrong button here
                // would mutate persistent Revit state.
            }

            var childText = CollectChildText(hWnd, budget, ref truncated);

            results.Add(new WindowInfo(title, className, childText));
            return true;
        }

        try
        {
            EnumWindows(TopLevelCallback, IntPtr.Zero);
        }
        catch
        {
            // Diagnosis-only feature: an empty inventory is a safe, honest degrade -- never worth
            // risking the caller's own poll/execute response over. Truncated, though: an exception
            // mid-pass means an unknown amount was never enumerated. Any dismissals already posted before
            // the fault stand -- WM_CLOSE was sent and onDismissed already fired for each, independent of
            // this return value.
            return new WindowInventorySnapshot(Array.Empty<WindowInfo>(), Truncated: true);
        }

        return new WindowInventorySnapshot(results, truncated);
    }

    // §07 v2: perform the allowlist entry's per-signature dismiss action. Returns true only if the action
    // was actually carried out (WM_CLOSE posted, or the named button found and clicked). Any other outcome
    // returns false so the caller inventories the window as present rather than reporting a phantom dismiss.
    private static bool TryDismiss(IntPtr hWnd, DialogDismissAction action) => action.Kind switch
    {
        DialogDismissKind.PostClose => TryPostClose(hWnd),
        DialogDismissKind.ClickButton => TryClickNamedButton(hWnd, action.ButtonText),
        _ => false,
    };

    // §07 v2 auto-dismiss action: PostMessage (asynchronous, fire-and-forget) NOT SendMessage -- a
    // blocking send would re-introduce the very UI-thread block #138 removed by capping the pass. WM_CLOSE
    // asks the dialog to close as if its X were clicked; it does NOT tick "do not show again" and does NOT
    // click any button. try/catch so a dismiss failure degrades to "not dismissed", never throwing out of
    // enumeration.
    private static bool TryPostClose(IntPtr hWnd)
    {
        try
        {
            return PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    // §07 v2 ClickButton action: find this dialog's button whose caption matches buttonText and post
    // BM_CLICK to it. SAFE-FAILING BY DESIGN: if no matching button is found (or its text can't be read
    // within budget), NOTHING is clicked and false is returned. This action exists precisely for dialogs
    // whose other buttons mutate persistent state, so a wrong or guessed click is worse than not acting --
    // there is deliberately no fallback to WM_CLOSE or to a default button. The button's text read uses the
    // same bounded WM_GETTEXT as the inventory; a live modal pumps its own message loop, so its buttons
    // answer even while Revit's main UI thread is parked in that modal.
    private static bool TryClickNamedButton(IntPtr parent, string? buttonText)
    {
        if (string.IsNullOrEmpty(buttonText))
        {
            return false;
        }

        var wanted = NormalizeButtonText(buttonText);
        var target = IntPtr.Zero;

        bool ButtonCallback(IntPtr hWnd, IntPtr lParam)
        {
            if (!string.Equals(GetClassNameOf(hWnd), ButtonClassName, StringComparison.OrdinalIgnoreCase))
            {
                return true; // not a button control
            }

            var text = GetWindowText(hWnd, out var timedOut);
            if (timedOut)
            {
                return true; // couldn't read it in time -- never guess; keep looking / give up safely
            }

            if (string.Equals(NormalizeButtonText(text), wanted, StringComparison.OrdinalIgnoreCase))
            {
                target = hWnd;
                return false; // found it -- stop enumerating
            }

            return true;
        }

        try
        {
            EnumChildWindows(parent, ButtonCallback, IntPtr.Zero);
        }
        catch
        {
            return false;
        }

        if (target == IntPtr.Zero)
        {
            return false; // named button not present on this dialog -- do nothing
        }

        try
        {
            return PostMessage(target, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    // Button captions carry a mnemonic ampersand ("&Cancel") and can be padded; the allowlist stores the
    // human label ("Cancel"). Strip ampersands and trim so the match is against the visible caption. (A
    // literal ampersand in a caption is "&&"; none of the §07 buttons use one, so a plain strip is fine.)
    private static string NormalizeButtonText(string text) => text.Replace("&", "").Trim();

    private static IReadOnlyList<string> CollectChildText(IntPtr parent, System.Diagnostics.Stopwatch budget, ref bool truncated)
    {
        var texts = new List<string>();
        var timedOutChildren = 0;
        var localTruncated = false;

        bool ChildCallback(IntPtr hWnd, IntPtr lParam)
        {
            if (budget.ElapsedMilliseconds > OverallBudgetMs)
            {
                localTruncated = true;
                return false; // stop enumerating -- see OverallBudgetMs
            }

            var text = GetWindowText(hWnd, out var timedOut);
            if (timedOut)
            {
                // Counted and summarized below rather than one placeholder per child: a busy UI
                // thread times out for EVERY child, and hundreds of identical placeholder lines
                // would bloat the §01 notice without adding information.
                timedOutChildren++;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                texts.Add(text);
            }

            return true;
        }

        try
        {
            EnumChildWindows(parent, ChildCallback, IntPtr.Zero);
        }
        catch
        {
            truncated = true;
            return texts;
        }

        if (timedOutChildren > 0)
        {
            texts.Add($"<{timedOutChildren} child window(s): text unavailable within budget>");
            localTruncated = true;
        }

        if (localTruncated)
        {
            truncated = true;
        }

        return texts;
    }

    // LIVE FINDING (the first end-to-end execution-lifecycle harness test caught this on its first
    // run): GetWindowTextW against a window owned by the CALLING PROCESS does not read the cached
    // title -- Win32 documents that it sends WM_GETTEXT and BLOCKS until the owning thread processes
    // it. Every window this inventory inspects is owned by Revit's UI thread, and this inventory runs
    // precisely when that thread is NOT pumping (the execute/poll timeout path, PRD §07) -- most
    // commonly because an ordinary long-running script is looping on it, no dialog anywhere. The
    // unbounded read therefore deadlocked the §07 diagnostic against the very condition it exists to
    // diagnose: the dispatcher's pending/running answer never got written, the broker's wire budget
    // expired (wire-call-failed), and the instance stranded busy. PRD §07's "this diagnostic itself
    // is always reachable" claim only becomes true with a bounded read: SendMessageTimeout with
    // SMTO_ABORTIFHUNG (returns immediately once the thread is deemed hung) and a small per-window
    // budget -- a timed-out read is REPORTED as such by the caller (timedOut out-param), never
    // silently dropped: an honest partial inventory beats a hung answer, and honesty means saying
    // which parts are missing.
    // #136, measured live: a single WM_GETTEXT against a script-blocked UI thread took 1744ms to return
    // here despite PerWindowTextTimeoutMs=100 -- SMTO_ABORTIFHUNG only short-circuits once Windows has
    // flagged the thread "hung" (~5s of no message pumping), and before that the send blocks well past
    // uTimeout. So this per-window bound is soft, the between-window OverallBudgetMs check cannot interrupt
    // a read already in flight, and the caller (RequestDispatcher) puts a HARD wall-clock cap around the
    // whole pass rather than trusting either bound to protect the wire response.
    private static string GetWindowText(IntPtr hWnd, out bool timedOut)
    {
        var buffer = new StringBuilder(MaxLength);
        if (SendMessageTimeoutW(hWnd, WM_GETTEXT, (IntPtr)MaxLength, buffer, SMTO_ABORTIFHUNG | SMTO_BLOCK, PerWindowTextTimeoutMs, out _) == IntPtr.Zero)
        {
            timedOut = true;
            return "";
        }

        timedOut = false;
        return buffer.ToString();
    }

    private static string GetClassNameOf(IntPtr hWnd)
    {
        var buffer = new StringBuilder(MaxLength);
        GetClassNameW(hWnd, buffer, MaxLength);
        return buffer.ToString();
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
