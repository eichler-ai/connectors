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
    private const uint SMTO_BLOCK = 0x0001;
    private const uint SMTO_ABORTIFHUNG = 0x0008;
    private const uint PerWindowTextTimeoutMs = 100;
    private const long OverallBudgetMs = 2000;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public IReadOnlyList<WindowInfo> EnumerateOwnedTopLevelWindows()
    {
        var currentProcessId = (uint)Environment.ProcessId;
        var results = new List<WindowInfo>();
        var budget = System.Diagnostics.Stopwatch.StartNew();

        bool TopLevelCallback(IntPtr hWnd, IntPtr lParam)
        {
            if (budget.ElapsedMilliseconds > OverallBudgetMs)
            {
                return false; // stop enumerating -- see OverallBudgetMs
            }

            GetWindowThreadProcessId(hWnd, out var owningProcessId);
            if (owningProcessId != currentProcessId)
            {
                return true;
            }

            var title = GetWindowText(hWnd);
            var className = GetClassNameOf(hWnd);
            var childText = CollectChildText(hWnd, budget);

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
            // risking the caller's own poll/execute response over.
            return Array.Empty<WindowInfo>();
        }

        return results;
    }

    private static IReadOnlyList<string> CollectChildText(IntPtr parent, System.Diagnostics.Stopwatch budget)
    {
        var texts = new List<string>();

        bool ChildCallback(IntPtr hWnd, IntPtr lParam)
        {
            if (budget.ElapsedMilliseconds > OverallBudgetMs)
            {
                return false; // stop enumerating -- see OverallBudgetMs
            }

            var text = GetWindowText(hWnd);
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
            return Array.Empty<string>();
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
    // expired (wire_call_failed), and the instance stranded busy. PRD §07's "this diagnostic itself
    // is always reachable" claim only becomes true with a bounded read: SendMessageTimeout with
    // SMTO_ABORTIFHUNG (returns immediately once the thread is deemed hung) and a small per-window
    // budget, degrading that window's text to "" -- an honest partial inventory beats a hung answer.
    private static string GetWindowText(IntPtr hWnd)
    {
        var buffer = new StringBuilder(MaxLength);
        if (SendMessageTimeoutW(hWnd, WM_GETTEXT, (IntPtr)MaxLength, buffer, SMTO_ABORTIFHUNG | SMTO_BLOCK, PerWindowTextTimeoutMs, out _) == IntPtr.Zero)
        {
            return "";
        }

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
