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
    private const uint SMTO_BLOCK = 0x0001;
    private const uint SMTO_ABORTIFHUNG = 0x0008;
    private const uint PerWindowTextTimeoutMs = 100;
    private const long OverallBudgetMs = 2000;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>Placeholder for a title whose WM_GETTEXT read timed out -- reported, not dropped (PRD §01).</summary>
    internal const string TextUnavailablePlaceholder = "<text unavailable within budget>";

    public WindowInventorySnapshot EnumerateOwnedTopLevelWindows(
        Func<string, string, bool> shouldDismiss,
        Action<DismissedDialog> onDismissed)
    {
        var currentProcessId = (uint)Environment.ProcessId;
        var results = new List<WindowInfo>();
        var dismissed = new List<DismissedDialog>();
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

            // §07 v2: consult the Core-owned allowlist. A match is CLOSED (fire-and-forget WM_CLOSE) and
            // recorded as dismissed instead of being listed as a present window -- it is on its way out, so
            // reporting it as still-present would be misleading. Only the plain title is used to match, so
            // a match with a timed-out title never happens (the placeholder never matches the allowlist).
            if (shouldDismiss(className, title))
            {
                if (TryPostClose(hWnd))
                {
                    var dd = new DismissedDialog(className, title);
                    dismissed.Add(dd);
                    onDismissed(dd); // side channel that survives the caller's #138 wire-budget abandon
                    return true;
                }

                // Post failed: degrade to "not dismissed" and fall through to inventory it as present,
                // so a benign but un-closable dialog is still surfaced for manual triage (same best-effort
                // posture as the rest of this class).
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
            // mid-pass means an unknown amount was never enumerated. Any dismissals already posted
            // before the fault stand -- WM_CLOSE was already sent -- so report them.
            return new WindowInventorySnapshot(Array.Empty<WindowInfo>(), Truncated: true, dismissed);
        }

        return new WindowInventorySnapshot(results, truncated, dismissed);
    }

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
