using MCPBridge.Core.Execution;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

public class LruCacheTests
{
    [Fact]
    public void SetThenTryGet_ReturnsValue()
    {
        var cache = new LruCache<string, int>(capacity: 4);
        cache.Set("a", 1);

        Assert.True(cache.TryGet("a", out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void TryGet_MissingKey_ReturnsFalse()
    {
        var cache = new LruCache<string, int>(capacity: 4);
        Assert.False(cache.TryGet("missing", out _));
    }

    [Fact]
    public void CapacityExceeded_EvictsLeastRecentlyUsed()
    {
        var cache = new LruCache<string, int>(capacity: 2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.TryGet("a", out _); // touch a, making b the least-recently-used
        cache.Set("c", 3); // should evict b, not a

        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
    }

    [Fact]
    public void Set_ExistingKey_UpdatesValue_AndCountsAsRecentUse()
    {
        var cache = new LruCache<string, int>(capacity: 2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.Set("a", 10); // re-set a; b is now least-recently-used
        cache.Set("c", 3); // should evict b

        Assert.True(cache.TryGet("a", out var value));
        Assert.Equal(10, value);
        Assert.False(cache.TryGet("b", out _));
    }

    [Fact]
    public void Count_ReflectsCurrentSize_BoundedByCapacity()
    {
        var cache = new LruCache<string, int>(capacity: 2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.Set("c", 3);

        Assert.Equal(2, cache.Count);
    }
}
