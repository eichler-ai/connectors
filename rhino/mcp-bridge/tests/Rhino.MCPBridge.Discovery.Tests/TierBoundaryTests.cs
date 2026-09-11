using System;
using System.Linq;
using Rhino.MCPBridge.Core.Discovery;
using Xunit;

namespace Rhino.MCPBridge.Discovery.Tests;

/// <summary>
/// The tier-boundary invariant from the ported ranker (Revit issue #80): a row admitted to tier 2 but
/// scoring ZERO relevance must never be emitted, because it would outrank every tier-3 FTS match.
///
/// <para>Tier 2's floor is <c>500 + CoreBoost</c> and tier 3 is asymptotically bounded below 500, so a row
/// admitted to tier 2 and then scored at 0 sits above the entire FTS ranking however strong those matches
/// are. It is reachable when <b>admission and scoring disagree about word boundaries</b>: admission is
/// <c>LOWER(name) LIKE '%token%'</c> against the raw stored name, while <c>IdentifierRelevance</c> scores
/// against <c>SplitWords</c> — so a token that is a contiguous substring of a name but in no word-part
/// (Revit's example was "lineweight" vs <c>SplitWords("LineWeight") = ["line","weight"]</c>) is admitted
/// yet explained by nothing.</para>
///
/// <para>These assert the INVARIANT (no zero-relevance tier-2 row; a weak-but-nonzero match still reaches
/// tier 2) over the real RhinoCommon corpus rather than reproducing Revit's exact offending rows — the
/// invariant is corpus-agnostic, and it is what a future ranker change could break. Both queries below
/// carry a positive control so a broken/empty corpus cannot make them pass vacuously.</para>
/// </summary>
public class TierBoundaryTests
{
    /// <summary>
    /// A tolerance around tier 2's floor, wide enough to catch a row of either assembly kind and far
    /// narrower than the smallest score a genuine match can earn.
    ///
    /// <para>Both bounds come from production constants rather than literals. An earlier version
    /// hard-coded 500.5, which was a proxy for "relevance == 0" and not the property itself: changing
    /// <c>CoreBoost</c> or the tier-2 base would move every zero-relevance row somewhere else, the filter
    /// would match nothing, and the test would stay green while the invariant was fully broken. It also
    /// missed non-core rows entirely, which land at 500.0 -- not reachable while this fixture syncs only
    /// core assemblies, but issue #91 made an add-in assembly a real production configuration.</para>
    ///
    /// <para>The window is safe because the smallest NONZERO relevance a row can earn is
    /// <c>0.75 x 0.15 x 0.9 = 0.10125</c>, worth about 25 points -- so nothing genuine can land inside it.</para>
    /// </summary>
    private const double FloorTolerance = 0.0001;

    /// <summary>The zero-relevance-in-tier-2 half of the invariant, checked over RhinoCommon. Unlike the
    /// Revit corpus this never self-skips: <see cref="RealRhinoCorpus"/> builds from the managed RhinoCommon
    /// NuGet, so the test always runs.</summary>
    [Fact]
    public void NoTierTwoRowIsEmittedWithZeroRelevance()
    {
        var cache = RealRhinoCorpus.Shared;

        var results = cache.Search("create mesh from box", namespaceFilter: null).ToList();

        // POSITIVE CONTROL. Without it, a sync that no-ops or an admission predicate that stops matching
        // makes the assertion below trivially true: zero rows means zero rows at the floor. Test 2 has
        // this control; this one did not until review pointed it out.
        Assert.True(results.Count > 0, "the query matched nothing at all, so the fixture is broken");

        var atTheFloor = results
            .Where(r => r.Score >= DiscoveryCache.TierTwoFloor - FloorTolerance
                     && r.Score <= DiscoveryCache.TierTwoFloor + DiscoveryCache.CoreAssemblyBoost + FloorTolerance)
            .Select(r => $"{r.Member.DeclaringType}.{r.Member.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            atTheFloor.Count == 0,
            $"{atTheFloor.Count} of {results.Count} rows sit at the tier-2 floor " +
            $"({DiscoveryCache.TierTwoFloor}-{DiscoveryCache.TierTwoFloor + DiscoveryCache.CoreAssemblyBoost}), " +
            "meaning the query's words explain nothing about them, yet they outrank every tier-3 match in " +
            "the corpus:\n  " + string.Join("\n  ", atTheFloor.Take(20)));
    }

    /// <summary>The other half, and the reason this is not simply "drop low scores": a WEAK but genuine
    /// match must still reach tier 2. Over RhinoCommon a <c>Millimeter</c> member earns a prefix credit for
    /// "millimeters", so it is explained — barely — and must stay admitted; asserting the boundary from
    /// both sides is what makes it a boundary rather than a threshold.</summary>
    [Fact]
    public void AWeakButNonZeroMatchStillReachesTierTwo()
    {
        var cache = RealRhinoCorpus.Shared;

        var millimeters = cache.Search("millimeters", namespaceFilter: null)
            .Where(r => r.Member.Name.Contains("Millimeter", StringComparison.Ordinal))
            .ToList();

        Assert.True(millimeters.Count > 0, "no Millimeter member matched at all; the fixture assumption is wrong");
        Assert.True(
            millimeters.Any(r => r.Score > DiscoveryCache.TierTwoFloor + DiscoveryCache.CoreAssemblyBoost),
            "every Millimeter row fell out of tier 2. A weak-but-nonzero match must still be admitted -- " +
            "issue #80 drops rows the query explains NOTHING about, not rows it explains poorly.");
    }
}
