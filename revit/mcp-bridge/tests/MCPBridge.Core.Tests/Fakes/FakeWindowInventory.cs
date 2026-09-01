using System;
using System.Collections.Generic;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Tests.Fakes;

public sealed class FakeWindowInventory : IWindowInventory
{
    public IReadOnlyList<WindowInfo> Windows { get; set; } = Array.Empty<WindowInfo>();
    public bool Truncated { get; set; }
    public bool ThrowOnEnumerate { get; set; }

    // #136: simulate the real pass's blocking cost (Win32WindowInventory reads window text against a busy
    // UI thread). When set, EnumerateOwnedTopLevelWindows blocks this long before returning, and records
    // that it was actually entered -- so a test can assert the caller stopped WAITING for it.
    public TimeSpan BlockFor { get; set; } = TimeSpan.Zero;
    public int EnumerateCallCount;

    public WindowInventorySnapshot EnumerateOwnedTopLevelWindows()
    {
        System.Threading.Interlocked.Increment(ref EnumerateCallCount);
        if (BlockFor > TimeSpan.Zero)
        {
            System.Threading.Thread.Sleep(BlockFor);
        }

        if (ThrowOnEnumerate)
        {
            throw new InvalidOperationException("simulated EnumWindows failure");
        }

        return new WindowInventorySnapshot(Windows, Truncated);
    }
}
