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
    public void ListNamespaces_ExcludesTheEmptyGlobalNamespace()
    {
        // Independent PR review finding: list_functions' tree has no way to scope INTO an empty-string
        // namespace (namespaceFilter treats "" and null identically), so leaving it in the namespaces tier
        // created an entry an agent could see but never drill into -- a dead end, not just noise.
        using var cache = NewCache();
        cache.Sync(new[] { ("core", typeof(global::GlobalNamespaceType).Assembly) });

        var namespaces = cache.ListNamespaces();

        Assert.DoesNotContain(namespaces, n => n.Namespace == "");
    }

    [Fact]
    public void ListNamespaces_CoreNamespacesSortBeforeAddinNamespaces_RegardlessOfAlphabeticalOrder()
    {
        // Live finding (coverage-plan Phase A session): on a real dev VM with ~690 loaded namespaces, a
        // straight alphabetical ORDER BY buries every Autodesk.Revit.* namespace behind dozens of pages of
        // third-party add-in noise. Fixed to sort core-kind namespaces first; this is the regression guard.
        //
        // MCPBridge.Core.Discovery (DiscoveryCache's own namespace) sorts ALPHABETICALLY BEFORE
        // MCPBridge.Discovery.Tests.Fixtures ('C' < 'D') -- so the two are synced below with kinds swapped
        // from what their names suggest (Core.Discovery as "addin", Fixtures as "core"), which means this
        // test can only pass if kind, not alphabetical position, actually drives the order. MCPBridge.Core.dll
        // has no XML-doc sidecar of its own; its types still count as documented via Sync's own
        // no-sidecar-still-documented fallback, so they still appear in ListNamespaces() to sort against.
        using var cache = NewCache();
        cache.Sync(new[]
        {
            ("addin", typeof(DiscoveryCache).Assembly),
            ("core", typeof(Widget).Assembly),
        });

        var namespaces = cache.ListNamespaces().Select(n => n.Namespace).ToList();
        var coreIndex = namespaces.IndexOf(FixturesNamespace);
        var addinIndex = namespaces.FindIndex(n => n.StartsWith("MCPBridge.Core", System.StringComparison.Ordinal));

        Assert.True(coreIndex >= 0, "expected the core-kind fixtures namespace to be present");
        Assert.True(addinIndex >= 0, "expected the addin-kind MCPBridge.Core.* namespace to be present");
        Assert.True(coreIndex < addinIndex,
            $"core namespace at index {coreIndex} should sort before addin namespace at index {addinIndex} despite 'MCPBridge.Core...' being alphabetically earlier");
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
    public void Search_Fts5Tier_StrongerMatchScoresHigherThanWeakerMatch()
    {
        // Independent PR review finding: bm25() returns a negative value, more negative = better match. An
        // earlier version of the tier-3 score normalization had the fold backwards -- OrderByDescending
        // (DiscoveryService's own ranking) actually surfaced the WEAKEST tier-3 hits first. "padding" appears
        // ~28 times in LongSummaryMethod's summary (a strong, high-term-frequency match) and exactly once in
        // AdjustLayout's (a weak match) -- neither name contains "padding", so both can only be found via the
        // FTS5 tier, making this a clean same-query, same-tier comparison.
        using var cache = NewCache();
        cache.Sync(new[] { ("core", typeof(Widget).Assembly) });

        var results = cache.Search("padding", namespaceFilter: null).OrderByDescending(r => r.Score).ToList();

        var strong = results.Single(r => r.Member.Name == "LongSummaryMethod");
        var weak = results.Single(r => r.Member.Name == "AdjustLayout");
        Assert.True(strong.Score > weak.Score, $"stronger match ({strong.Score}) must outrank weaker match ({weak.Score})");
    }

    [Fact]
    public void Search_FullyQualifiedTypeDotMember_StillResolvesTierOne()
    {
        // Independent PR review finding: a query copied verbatim from a describe_function result or from
        // Revit's own docs is naturally fully-qualified ("Namespace.Type.Member"), but types.name only ever
        // stores the bare type name -- without stripping to the type token's own last dotted segment, this
        // fell all the way through to the loose tier-3 fallback instead of resolving as an exact match.
        using var cache = NewCache();
        cache.Sync(new[] { ("core", typeof(Widget).Assembly) });

        var results = cache.Search(FixturesNamespace + ".Gadget.Run", namespaceFilter: null);

        var top = results.OrderByDescending(r => r.Score).First();
        Assert.Equal("Run", top.Member.Name);
        Assert.True(top.Score >= 1000);
    }

    [Fact]
    public void Search_FullyQualifiedQuery_DisambiguatesFromSameBareNameInAnotherNamespace()
    {
        // Independent PR review finding (2nd round, M3): an earlier version of the fully-qualified-query
        // fix stripped straight to the bare type name unconditionally, discarding the one piece of
        // information that made an already-qualified query unambiguous -- it would tie at score 1000
        // against Fixtures.Other.Gadget.Run (same bare name, same member name, different namespace --
        // simulating two add-ins vendoring a same-named helper type) instead of resolving to the SPECIFIC
        // one actually named.
        using var cache = NewCache();
        cache.Sync(new[] { ("core", typeof(Widget).Assembly) });

        var results = cache.Search(FixturesNamespace + ".Gadget.Run", namespaceFilter: null);
        var exactMatches = results.Where(r => r.Score >= 1000).ToList();

        var only = Assert.Single(exactMatches);
        Assert.Equal(FixturesNamespace, only.Member.Namespace);
        Assert.Equal("MCPBridge.Discovery.Tests.Fixtures.Gadget", only.Member.DeclaringType);
    }

    [Fact]
    public void Search_NamespaceFilter_ExcludesOtherNamespaces()
    {
        using var cache = NewCache();
        cache.Sync(new[] { ("core", typeof(Widget).Assembly) });

        var results = cache.Search("Do", namespaceFilter: FixturesNamespace);

        Assert.DoesNotContain(results, r => r.Member.Namespace != FixturesNamespace);
    }

    [Fact]
    public void Search_CoreAssembly_OutranksAnOtherwiseIdenticalAddinMatch()
    {
        // Live finding (coverage-plan Phase A session): an unscoped search_functions query can return
        // zero core-Revit hits at all, buried under third-party add-in noise -- nothing in Search() used
        // the assemblies.kind column any query path already carried. Same fixture assembly synced under
        // both kinds in SEPARATE caches, so this isolates CoreBoost itself rather than depending on two
        // genuinely different assemblies happening to collide on a type/member name.
        using var coreCache = NewCache();
        coreCache.Sync(new[] { ("core", typeof(Widget).Assembly) });
        using var addinCache = NewCache();
        addinCache.Sync(new[] { ("addin", typeof(Widget).Assembly) });

        var coreScore = coreCache.Search("Gadget.Run", namespaceFilter: null).OrderByDescending(r => r.Score).First().Score;
        var addinScore = addinCache.Search("Gadget.Run", namespaceFilter: null).OrderByDescending(r => r.Score).First().Score;

        Assert.True(coreScore > addinScore, $"core match ({coreScore}) must outrank an otherwise-identical addin match ({addinScore})");
        // The boost must stay small enough to never cross a tier boundary (tiers are 500 points apart) --
        // a weak core match must never leapfrog a genuinely stronger add-in match in a lower-numbered tier.
        Assert.True(coreScore - addinScore < 1.0, $"boost ({coreScore - addinScore}) is large enough to risk crossing a tier boundary");
    }
}
