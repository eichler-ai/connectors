using System;
using System.Collections.Generic;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Tests.Fakes;

public sealed class FakeWindowInventory : IWindowInventory
{
    public IReadOnlyList<WindowInfo> Windows { get; set; } = Array.Empty<WindowInfo>();
    public bool Truncated { get; set; }
    public bool ThrowOnEnumerate { get; set; }

    public WindowInventorySnapshot EnumerateOwnedTopLevelWindows()
    {
        if (ThrowOnEnumerate)
        {
            throw new InvalidOperationException("simulated EnumWindows failure");
        }

        return new WindowInventorySnapshot(Windows, Truncated);
    }
}
