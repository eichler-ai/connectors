using System;
using System.Collections.Generic;
using System.Threading;
using Rhino.MCPBridge.Core.Diagnostics;

namespace Rhino.MCPBridge.Core.Tests.Fakes;

/// <summary>Test double for the §08 window inventory (IViewCapture-fake pattern). Lets a test set the
/// windows a pass returns, simulate a truncated/failed/slow pass, and count calls.</summary>
public sealed class FakeWindowInventory : IWindowInventory
{
    public IReadOnlyList<WindowInfo> Windows { get; set; } = Array.Empty<WindowInfo>();
    public bool Truncated { get; set; }
    public bool ThrowOnEnumerate { get; set; }

    /// <summary>Simulate the real pass's blocking cost, so a test can assert the caller's hard cap wins.</summary>
    public TimeSpan BlockFor { get; set; } = TimeSpan.Zero;

    public int EnumerateCallCount;

    public WindowInventorySnapshot EnumerateOwnedTopLevelWindows()
    {
        Interlocked.Increment(ref EnumerateCallCount);
        if (BlockFor > TimeSpan.Zero)
        {
            Thread.Sleep(BlockFor);
        }

        if (ThrowOnEnumerate)
        {
            throw new InvalidOperationException("simulated window-enumeration failure");
        }

        return new WindowInventorySnapshot(Windows, Truncated);
    }
}
