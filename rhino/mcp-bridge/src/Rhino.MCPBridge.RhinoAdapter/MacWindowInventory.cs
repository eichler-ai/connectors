using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Rhino.MCPBridge.Core.Diagnostics;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// macOS implementation of the PRD §08 v1 diagnostic. Uses Core Graphics
/// <c>CGWindowListCopyWindowInfo</c> — NOT <c>NSApplication.windows</c>: AppKit is main-thread-only, and
/// this diagnostic must run off the main thread (the whole point is that the main thread may be blocked by
/// the very modal being inspected; implementation-plan.md's "NSApplication.windows via Eto" line is
/// superseded for this reason). CGWindowList enumerates the window server off-main with no AppKit
/// constraint, filtered here to this process's own windows.
///
/// <para><b>Not yet verified against a live Mac Rhino</b> — the shared licence keeps Mac Rhino down during
/// the Windows pass, so this is exercised only by the Core tier-1 tests through the fake. It is included
/// so the seam has both platform legs and the architecture (off-main, CGWindowList) is right; the Mac live
/// pass confirms it when the licence frees up.</para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacWindowInventory : IWindowInventory
{
    // kCGWindowListOptionOnScreenOnly (1) | kCGWindowListExcludeDesktopElements (0x10): on-screen windows
    // that are not desktop chrome. relativeToWindow = kCGNullWindowID (0).
    private const uint ListOption = 0x1 | 0x10;

    public WindowInventorySnapshot EnumerateOwnedTopLevelWindows()
    {
        var pid = Environment.ProcessId;
        var results = new List<WindowInfo>();
        var array = IntPtr.Zero;
        var keyPid = IntPtr.Zero;
        var keyName = IntPtr.Zero;
        var keyLayer = IntPtr.Zero;
        try
        {
            array = CGWindowListCopyWindowInfo(ListOption, 0);
            if (array == IntPtr.Zero)
            {
                return new WindowInventorySnapshot(results, Truncated: true);
            }

            keyPid = MakeCfString("kCGWindowOwnerPID");
            keyName = MakeCfString("kCGWindowName");
            keyLayer = MakeCfString("kCGWindowLayer");

            var count = CFArrayGetCount(array);
            for (long i = 0; i < count; i++)
            {
                var dict = CFArrayGetValueAtIndex(array, i);
                if (dict == IntPtr.Zero || GetCfInt(dict, keyPid) != pid)
                {
                    continue;
                }

                var title = GetCfString(dict, keyName);
                var layer = GetCfInt(dict, keyLayer);
                // The window server has no "class"; report the layer as the closest analogue. A normal
                // document/dialog window is layer 0; a modal/panel sits above it. IsMainWindow is a
                // best-effort heuristic (layer 0, first seen) pending live verification of a reliable
                // main-window signal on Mac Rhino.
                results.Add(new WindowInfo(title ?? string.Empty, $"CGWindowLayer={layer}", IsMainWindow: false, IsVisible: true));
            }

            MarkMainWindowHeuristic(results);
            return new WindowInventorySnapshot(results, Truncated: false);
        }
        catch
        {
            return new WindowInventorySnapshot(results, Truncated: true);
        }
        finally
        {
            if (keyPid != IntPtr.Zero) CFRelease(keyPid);
            if (keyName != IntPtr.Zero) CFRelease(keyName);
            if (keyLayer != IntPtr.Zero) CFRelease(keyLayer);
            if (array != IntPtr.Zero) CFRelease(array);
        }
    }

    /// <summary>Best-effort main-window mark: the first layer-0 window (`CGWindowLayer=0`) is taken as
    /// Rhino's main frame. Heuristic only, pending live verification.</summary>
    private static void MarkMainWindowHeuristic(List<WindowInfo> windows)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            if (windows[i].ClassName == "CGWindowLayer=0")
            {
                windows[i] = windows[i] with { IsMainWindow = true };
                return;
            }
        }
    }

    private static long GetCfInt(IntPtr dict, IntPtr key)
    {
        var value = CFDictionaryGetValue(dict, key);
        if (value == IntPtr.Zero)
        {
            return -1;
        }

        // kCFNumberSInt64Type = 4
        return CFNumberGetValue(value, 4, out long result) ? result : -1;
    }

    private static string? GetCfString(IntPtr dict, IntPtr key)
    {
        var value = CFDictionaryGetValue(dict, key);
        if (value == IntPtr.Zero)
        {
            return null;
        }

        // kCFStringEncodingUTF8 = 0x08000100
        var maxBytes = CFStringGetMaximumSizeForEncoding(CFStringGetLength(value), 0x08000100) + 1;
        var buffer = new byte[maxBytes];
        return CFStringGetCString(value, buffer, buffer.Length, 0x08000100)
            ? System.Text.Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0) is var n && n >= 0 ? n : buffer.Length).Trim()
            : null;
    }

    private static IntPtr MakeCfString(string s) =>
        CFStringCreateWithCString(IntPtr.Zero, s, 0x08000100);

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreGraphics)] private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    [DllImport(CoreFoundation)] private static extern long CFArrayGetCount(IntPtr array);
    [DllImport(CoreFoundation)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [DllImport(CoreFoundation)] private static extern bool CFNumberGetValue(IntPtr number, long type, out long value);
    [DllImport(CoreFoundation)] private static extern long CFStringGetLength(IntPtr str);
    [DllImport(CoreFoundation)] private static extern long CFStringGetMaximumSizeForEncoding(long length, uint encoding);
    [DllImport(CoreFoundation)] private static extern bool CFStringGetCString(IntPtr str, byte[] buffer, long bufferSize, uint encoding);
    [DllImport(CoreFoundation, CharSet = CharSet.Ansi)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string s, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);
}
