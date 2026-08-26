using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Real Win32 EnumWindows-based implementation (PRD §07 v1). Not unit-tested -- see
/// RevitTransactionAdapter's own doc comment for why. Called from RequestDispatcher's own thread (the
/// TCP/background thread), never Revit's UI thread, so it stays reachable even while that thread is
/// blocked behind a modal dialog -- that's the whole point of this fallback.
/// </summary>
public sealed class Win32WindowInventory : IWindowInventory
{
    private const int MaxLength = 512;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public IReadOnlyList<WindowInfo> EnumerateOwnedTopLevelWindows()
    {
        var currentProcessId = (uint)Environment.ProcessId;
        var results = new List<WindowInfo>();

        bool TopLevelCallback(IntPtr hWnd, IntPtr lParam)
        {
            GetWindowThreadProcessId(hWnd, out var owningProcessId);
            if (owningProcessId != currentProcessId)
            {
                return true;
            }

            var title = GetWindowText(hWnd);
            var className = GetClassNameOf(hWnd);
            var childText = CollectChildText(hWnd);

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

    private static IReadOnlyList<string> CollectChildText(IntPtr parent)
    {
        var texts = new List<string>();

        bool ChildCallback(IntPtr hWnd, IntPtr lParam)
        {
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

    private static string GetWindowText(IntPtr hWnd)
    {
        var buffer = new StringBuilder(MaxLength);
        GetWindowTextW(hWnd, buffer, MaxLength);
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
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
