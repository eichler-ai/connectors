using System;
using System.Collections.Generic;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Tests.Fakes;

public sealed class FakeWindowInventory : IWindowInventory
{
    public IReadOnlyList<WindowInfo> Windows { get; set; } = Array.Empty<WindowInfo>();
    public bool Truncated { get; set; }
    public bool ThrowOnEnumerate { get; set; }

    // §07 v2: a test configures the dialogs the real adapter would auto-dismiss (the fake does not run the
    // P/Invoke WM_CLOSE); each is delivered through onDismissed, the same side channel the real adapter uses.
    // Inspect LastShouldDismiss to assert which predicate the caller passed in.
    public IReadOnlyList<DismissedDialog> Dismissed { get; set; } = Array.Empty<DismissedDialog>();
    public Func<string, string, bool>? LastShouldDismiss { get; private set; }

    // #136: simulate the real pass's blocking cost (Win32WindowInventory reads window text against a busy
    // UI thread). When set, EnumerateOwnedTopLevelWindows blocks this long before returning, and records
    // that it was actually entered -- so a test can assert the caller stopped WAITING for it.
    public TimeSpan BlockFor { get; set; } = TimeSpan.Zero;
    public int EnumerateCallCount;

    // For DETERMINISTIC abandon tests (no timing race under parallel load): when set, the pass fires its
    // dismissals then blocks on this gate until the test releases it, so the caller's wire-budget Task.Delay
    // always wins the race regardless of thread-pool contention. The test signals it to free the thread.
    public System.Threading.ManualResetEventSlim? Gate { get; set; }

    public WindowInventorySnapshot EnumerateOwnedTopLevelWindows(
        Func<string, string, bool> shouldDismiss,
        Action<DismissedDialog> onDismissed)
    {
        LastShouldDismiss = shouldDismiss;
        System.Threading.Interlocked.Increment(ref EnumerateCallCount);

        // Fire the side channel BEFORE any block, mirroring the real adapter (which dismisses the modal
        // early in the pass) -- so a #138 abandon test still captures the dismissal.
        foreach (var d in Dismissed)
        {
            onDismissed(d);
        }

        Gate?.Wait();

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
