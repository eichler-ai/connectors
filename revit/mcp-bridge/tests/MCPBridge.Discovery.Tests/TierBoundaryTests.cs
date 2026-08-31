using System;
using System.Linq;
using MCPBridge.Core.Discovery;
using Xunit;

namespace MCPBridge.Discovery.Tests;

/// <summary>
/// Issue #80: a tier-2 row scoring ZERO relevance still outranked every tier-3 match in the corpus.
///
/// <para>Tier 2's floor is <c>500 + CoreBoost</c> and tier 3 is asymptotically bounded below 500, so a row
/// admitted to tier 2 and then scored at 0 sits above the entire FTS ranking however strong those matches
/// are. That is reachable because <b>admission and scoring disagree about word boundaries</b>: admission is
/// <c>LOWER(name) LIKE '%token%'</c> against the raw stored name, while <c>IdentifierRelevance</c> scores
/// against <c>SplitWords</c>. <c>SplitWords("LineWeight")</c> is <c>["line","weight"]</c>, so the token
/// "lineweight" is a contiguous substring of the raw name and is in no word-part at all.</para>
///
/// <para>Not covered by the ranking snapshot, and that is why this file exists rather than another corpus
/// row: the offending rows sit at the tier-2 floor, which puts them around rank 35 for the reported query
/// -- below the snapshot's depth of 10. A defect can be real, reproducible and still invisible to the
/// instrument built to catch ranking changes, so it needs an assertion on the INVARIANT rather than on a
/// leaderboard. Measured: 17 of that query's 548 rows, and the mutation message names them.</para>
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

    /// <summary>
    /// The query issue #80 reported, whose admitted-but-unexplained rows are the clearest instance.
    ///
    /// <para>Measured before the fix: <c>Category.GetLineWeight</c>, <c>Category.SetLineWeight</c>,
    /// <c>FilledRegionType.IsValidLineWeight</c>, <c>OverrideGraphicSettings.SetProjectionLineWeight</c>,
    /// <c>DWGImportOptions.GetLineWeights</c> and a dozen more -- 17 rows in all, every one at the floor
    /// and above the best tier-3 row.</para>
    /// </summary>
    [Fact]
    public void NoTierTwoRowIsEmittedWithZeroRelevance()
    {
        // Self-skips when this machine has no Revit for the TFM under test. Acceptable here ONLY because
        // RealRevitApiLoaderTests turns "Revit is installed but this family skipped anyway" into a red
        // build -- otherwise this is the reported-as-PASSED shape that has killed a test family twice.
        var built = RealRevitCorpus.TryBuild();
        if (built is null)
        {
            return;
        }

        using var context = built.Value.Context;
        using var cache = built.Value.Cache;

        var results = cache.Search("create lineweight", namespaceFilter: null).ToList();

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

    /// <summary>
    /// The other half of the invariant, and the reason this is not simply "drop low scores": a WEAK but
    /// genuine match must still reach tier 2. <c>UnitTypeId.Kilonewtons</c> earns a prefix credit for
    /// "kilonewton", so it is explained -- barely -- and #80 explicitly leaves that case alone.
    ///
    /// <para>Without this, the fix could be over-applied to a threshold ("drop anything below 0.1") and
    /// nothing would notice. Asserting the boundary from both sides is what makes it a boundary.</para>
    /// </summary>
    [Fact]
    public void AWeakButNonZeroMatchStillReachesTierTwo()
    {
        var built = RealRevitCorpus.TryBuild();
        if (built is null)
        {
            return;
        }

        using var context = built.Value.Context;
        using var cache = built.Value.Cache;

        var kilonewtons = cache.Search("create kilonewton", namespaceFilter: null)
            .Where(r => r.Member.Name.Contains("Kilonewton", StringComparison.Ordinal))
            .ToList();

        Assert.True(kilonewtons.Count > 0, "no Kilonewton member matched at all; the fixture assumption is wrong");
        Assert.True(
            kilonewtons.Any(r => r.Score > DiscoveryCache.TierTwoFloor + DiscoveryCache.CoreAssemblyBoost),
            "every Kilonewton row fell out of tier 2. A weak-but-nonzero match must still be admitted -- " +
            "issue #80 drops rows the query explains NOTHING about, not rows it explains poorly.");
    }
}
