using System.Linq;
using MCPBridge.Core.Discovery;
using MCPBridge.Discovery.Tests.Fixtures;
using Xunit;

namespace MCPBridge.Discovery.Tests;

/// <summary>
/// Coverage of <see cref="DiscoveryCache"/> itself -- Sync's diff/reconcile logic and the query methods
/// <see cref="DiscoveryService"/> composes into list_functions/search_functions/describe_function. Uses a
/// fresh in-memory (":memory:") cache per test, synced against this test assembly's own Fixtures/*.cs types
/// (the same portable, self-contained target <see cref="DiscoveryServiceTests"/> uses) -- real doc-comment
/// summaries via the compiler-emitted XML sidecar, so the FTS5 summary-only tier is exercisable without a
/// real RevitAPI.xml.
/// </summary>
public class DiscoveryCacheTests
{
    private const string FixturesNamespace = "MCPBridge.Discovery.Tests.Fixtures";

    private static DiscoveryCache NewCache() => new(":memory:");

    // ---------------------------------------------------------------------------------------------
    // Sync
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Sync_NewAssembly_InsertsTypesAndMembers()
    {
        using var cache = NewCache();

        var result = cache.Sync(new[] { ("core", typeof(Widget).Assembly) });

        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.Unchanged);
        Assert.Contains("Widget", cache.ListTypeNames(FixturesNamespace));
        Assert.Contains("Describe", cache.ListMemberNames(FixturesNamespace, "Widget"));
    }

    [Fact]
    public void Sync_UnchangedAssembly_NoOpsOnSecondCall()
    {
        using var cache = NewCache();
        cache.Sync(new[] { ("core", typeof(Widget).Assembly) });

        var result = cache.Sync(new[] { ("core", typeof(Widget).Assembly) });

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.Unchanged);
    }

    [Fact]
    public void Sync_ChangedAssembly_PurgesAndReinserts()
    {
        using var cache = NewCache();
        cache.Sync(new[] { ("core", typeof(Widget).Assembly) });
        cache.SetStoredHashForTesting(typeof(Widget).Assembly.Location, "deliberately-stale-hash");

        var result = cache.Sync(new[] { ("core", typeof(Widget).Assembly) });

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Removed);
        // The type/member surface is still there after the purge + re-reflect, not just the assembly row.
        Assert.Contains("Widget", cache.ListTypeNames(FixturesNamespace));
        Assert.Contains("Describe", cache.ListMemberNames(FixturesNamespace, "Widget"));
    }

    [Fact]
    public void Sync_GoneAssembly_CascadesDeleteOfItsTypesAndMembers()
    {
        using var cache = NewCache();
        cache.Sync(new[] { ("core", typeof(Widget).Assembly) });
        Assert.Contains("Widget", cache.ListTypeNames(FixturesNamespace)); // sanity: present before removal

        var result = cache.Sync(System.Array.Empty<(string, System.Reflection.Assembly)>());

        Assert.Equal(1, result.Removed);
        Assert.Empty(cache.ListNamespaces());
        Assert.Empty(cache.ListTypeNames(FixturesNamespace));
        Assert.Empty(cache.ListMemberNames(FixturesNamespace, "Widget"));
        Assert.False(cache.TypeExists(FixturesNamespace, "Widget"));
        // The FTS index must be cleaned up too, not just the relational tables -- a search that would have
        // matched a since-removed member must not find a dangling row.
        Assert.Empty(cache.Search("Describe", namespaceFilter: null));
    }

    // ---------------------------------------------------------------------------------------------
    // list_functions' three tiers, at the cache level
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ListNamespaces_ReturnsEveryDocumentedNamespaceWithACount()
    {
        using var cache = NewCache();
        cache.Sync(new[] { ("core", typeof(Widget).Assembly) });

        var namespaces = cache.ListNamespaces();

        Assert.Contains(namespaces, n => n.Namespace == FixturesNamespace && n.TypeCount > 0);
    }

    [Fact]
    public void ListTypeNames_ScopesToOneNamespaceOnly()
    {
        using var cache = NewCache();
        cache.Sync(new[] { ("core", typeof(Widget).Assembly) });

        var types = cache.ListTypeNames(FixturesNamespace);

        Assert.Contains("Widget", types);
        Assert.Contains("Gadget", types);
        Assert.DoesNotContain("Thing", types); // Fixtures.Other, not Fixtures itself.
    }

    [Fact]
    public void ListMemberNames_ScopesToOneTypeOnly_AndDedupesOverloads()
    {
        using var cache = NewCache();
        cache.Sync(new[] { ("core", typeof(Widget).Assembly) });

        var members = cache.ListMemberNames(FixturesNamespace, "Widget");

        Assert.Equal(1, members.Count(m => m == "Describe")); // 2 overloads -> 1 entry.
        Assert.DoesNotContain("Run", members); // Gadget's member, must not leak into Widget's list.
    }

    // ---------------------------------------------------------------------------------------------
    // search_functions tiering
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Search_ExactTypeDotMember_IsTierOne()
    {
        using var cache = NewCache();
        cache.Sync(new[] { ("core", typeof(Widget).Assembly) });

        var results = cache.Search("Gadget.Run", namespaceFilter: null);

        var top = results.OrderByDescending(r => r.Score).First();
        Assert.Equal("Run", top.Member.Name);
        Assert.True(top.Score >= 1000);
    }

    [Fact]
    public void Search_TokensAcrossTypeAndMemberName_OutranksSummaryOnlyMatch()
    {
        using var cache = NewCache();
        cache.Sync(new[] { ("core", typeof(Widget).Assembly) });

        // "widg desc" matches {type name, member name} for Widget.Describe (tier 2); "widget" alone (below)
        // covers the tier-3 (summary-only) case separately.
        var results = cache.Search("widg desc", namespaceFilter: null);

        var top = results.OrderByDescending(r => r.Score).First();
        Assert.Equal("Describe", top.Member.Name);
        Assert.InRange(top.Score, 500, 999);
    }

    [Fact]
    public void Search_SummaryOnlyMatch_StillFoundViaFts5Fallback()
    {
        using var cache = NewCache();
        cache.Sync(new[] { ("core", typeof(Widget).Assembly) });

        // "padding" appears only in Widget.LongSummaryMethod's XML-doc summary text (its own padding-padding-
        // padding filler, there specifically to exercise the 300-char truncation threshold) -- it's not a
        // substring of any type or member name in these fixtures, so this can ONLY be found via the FTS5
        // summary tier, never tier 1 or 2.
        var results = cache.Search("padding", namespaceFilter: null);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Member.Name == "LongSummaryMethod");
        Assert.All(results, r => Assert.True(r.Score < 500)); // never tier 1 or 2 -- name-based tiers would require "padding" to literally appear in a type/member name, which it doesn't.
    }

    [Fact]
    public void Search_NamespaceFilter_ExcludesOtherNamespaces()
    {
        using var cache = NewCache();
        cache.Sync(new[] { ("core", typeof(Widget).Assembly) });

        var results = cache.Search("Do", namespaceFilter: FixturesNamespace);

        Assert.DoesNotContain(results, r => r.Member.Namespace != FixturesNamespace);
    }
}
