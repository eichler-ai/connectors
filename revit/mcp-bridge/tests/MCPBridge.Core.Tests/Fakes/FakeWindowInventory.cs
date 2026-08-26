using System;
using System.Collections.Generic;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Tests.Fakes;

public sealed class FakeWindowInventory : IWindowInventory
{
    public IReadOnlyList<WindowInfo> Windows { get; set; } = Array.Empty<WindowInfo>();
    public bool ThrowOnEnumerate { get; set; }

    public IReadOnlyList<WindowInfo> EnumerateOwnedTopLevelWindows()
    {
        if (ThrowOnEnumerate)
        {
            throw new InvalidOperationException("simulated EnumWindows failure");
        }

        return Windows;
    }
}
