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
/// row: the offending rows score 500.5, which puts them at ranks 35-42 for the reported query -- below the
/// snapshot's depth of 10. A defect can be real, reproducible and still invisible to the instrument built
/// to catch ranking changes, so it needs an assertion on the INVARIANT rather than on a leaderboard.</para>
/// </summary>
public class TierBoundaryTests
{
    /// <summary>Tier 2's floor: <c>500 + CoreBoost</c>. A row landing exactly here earned no relevance at all.</summary>
    private const double TierTwoFloorForCore = 500.5;

    /// <summary>
    /// The query issue #80 reported, whose admitted-but-unexplained rows are the clearest instance.
    ///
    /// <para>Measured before the fix, at these exact scores: <c>Category.GetLineWeight</c>,
    /// <c>Category.SetLineWeight</c>, <c>FilledRegionType.IsValidLineWeight</c>,
    /// <c>OverrideGraphicSettings.SetProjectionLineWeight</c>, <c>DWGImportOptions.GetLineWeights</c> and a
    /// dozen more, all at 500.50 and all above the best tier-3 row.</para>
    /// </summary>
    [Fact]
    public void NoTierTwoRowIsEmittedWithZeroRelevance()
    {
        var loaded = RealRevitApiLoader.TryLoad();
        if (loaded is null)
        {
            return;
        }

        using var context = loaded.Value.Context;
        using var cache = new DiscoveryCache(":memory:");
        cache.Sync(new[] { ("core", loaded.Value.Assembly) });

        var atTheFloor = cache.Search("create lineweight", namespaceFilter: null)
            .Where(r => Math.Abs(r.Score - TierTwoFloorForCore) < 0.0001)
            .Select(r => $"{r.Member.DeclaringType}.{r.Member.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            atTheFloor.Count == 0,
            $"{atTheFloor.Count} rows sit at the tier-2 floor ({TierTwoFloorForCore}), meaning the query's " +
            "words explain nothing about them, yet they outrank every tier-3 match in the corpus:\n  " +
            string.Join("\n  ", atTheFloor.Take(20)));
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
        var loaded = RealRevitApiLoader.TryLoad();
        if (loaded is null)
        {
            return;
        }

        using var context = loaded.Value.Context;
        using var cache = new DiscoveryCache(":memory:");
        cache.Sync(new[] { ("core", loaded.Value.Assembly) });

        var kilonewtons = cache.Search("create kilonewton", namespaceFilter: null)
            .Where(r => r.Member.Name.Contains("Kilonewton", StringComparison.Ordinal))
            .ToList();

        Assert.True(kilonewtons.Count > 0, "no Kilonewton member matched at all; the fixture assumption is wrong");
        Assert.True(
            kilonewtons.Any(r => r.Score > TierTwoFloorForCore),
            "every Kilonewton row fell out of tier 2. A weak-but-nonzero match must still be admitted -- " +
            "issue #80 drops rows the query explains NOTHING about, not rows it explains poorly.");
    }
}
