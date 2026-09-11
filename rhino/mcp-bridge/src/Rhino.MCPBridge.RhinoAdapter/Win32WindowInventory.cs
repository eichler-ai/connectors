using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Rhino.MCPBridge.Core.Diagnostics;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// Real Win32 <c>EnumWindows</c> implementation of the PRD §08 v1 diagnostic (Windows). Called from the
/// dispatcher's connection thread, never Rhino's UI thread, so it stays reachable while that thread is
/// blocked behind a modal — the whole point of the fallback. Running off the UI thread is necessary but
/// not sufficient: reading same-process window TEXT re-introduces the UI-thread dependency through
/// WM_GETTEXT, so the text read is bounded per window (SendMessageTimeout) AND by an overall budget for
/// the whole pass (Revit's Win32WindowInventory learned both bounds live). Class name, visibility and the
/// main-window check read window state directly and do not send messages, so they are not bounded.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32WindowInventory : IWindowInventory
{
    private const int MaxTextLength = 512;
    private const uint WM_GETTEXT = 0x000D;
    private const uint SMTO_BLOCK = 0x0001;
    private const uint SMTO_ABORTIFHUNG = 0x0008;
    private const uint PerWindowTextTimeoutMs = 100;
    private const long OverallBudgetMs = 2000;

    /// <summary>A title whose WM_GETTEXT read timed out — reported, not dropped (PRD §01).</summary>
    internal const string TextUnavailablePlaceholder = "<text unavailable within budget>";

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public WindowInventorySnapshot EnumerateOwnedTopLevelWindows()
    {
        var currentProcessId = (uint)Environment.ProcessId;
        // MainWindowHandle enumerates top-level windows internally; safe off the UI thread. Captured once so
        // every window is compared against the same handle. IntPtr.Zero if Rhino has no main window yet.
        IntPtr mainWindow;
        try { mainWindow = Process.GetCurrentProcess().MainWindowHandle; }
        catch { mainWindow = IntPtr.Zero; }

        var results = new List<WindowInfo>();
        var budget = Stopwatch.StartNew();
        var truncated = false;

        bool Callback(IntPtr hWnd, IntPtr lParam)
        {
            if (budget.ElapsedMilliseconds > OverallBudgetMs)
            {
                truncated = true;
                return false; // stop enumerating
            }

            GetWindowThreadProcessId(hWnd, out var owningProcessId);
            if (owningProcessId != currentProcessId)
            {
                return true;
            }

            var title = GetWindowTextBounded(hWnd, out var timedOut);
            if (timedOut)
            {
                title = TextUnavailablePlaceholder;
                truncated = true;
            }

            results.Add(new WindowInfo(title, GetClassNameOf(hWnd), IsMainWindow: hWnd == mainWindow, IsVisible: IsWindowVisible(hWnd)));
            return true;
        }

        try { EnumWindows(Callback, IntPtr.Zero); }
        catch { truncated = true; } // never throw at the caller; report what was gathered

        return new WindowInventorySnapshot(results, truncated);
    }

    /// <summary>WM_GETTEXT via SendMessageTimeout so a busy (not yet OS-"hung") UI thread cannot block the
    /// read past the per-window bound; the overall budget caps the whole pass regardless.</summary>
    private static string GetWindowTextBounded(IntPtr hWnd, out bool timedOut)
    {
        var buffer = new StringBuilder(MaxTextLength);
        var ok = SendMessageTimeout(hWnd, WM_GETTEXT, (IntPtr)MaxTextLength, buffer, SMTO_BLOCK | SMTO_ABORTIFHUNG, PerWindowTextTimeoutMs, out _);
        timedOut = ok == IntPtr.Zero;
        return timedOut ? string.Empty : buffer.ToString();
    }

    private static string GetClassNameOf(IntPtr hWnd)
    {
        var buffer = new StringBuilder(MaxTextLength);
        var len = GetClassName(hWnd, buffer, MaxTextLength);
        return len > 0 ? buffer.ToString() : string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam, uint flags, uint timeoutMs, out IntPtr result);
}
