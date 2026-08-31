using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MCPBridge.Core.Discovery;
using Xunit;

namespace MCPBridge.Discovery.Tests;

/// <summary>
/// Issue #81: tier 3's core-first PRIMARY sort excluded add-in results entirely.
///
/// <para>The clause was <c>ORDER BY (a.kind != 'core'), bm25(members_fts) LIMIT @limit</c>, which sorts by
/// assembly kind BEFORE relevance under a limit. That makes kind a filter on candidate SELECTION rather
/// than a preference applied to ranking: once core alone supplies the whole budget, no add-in row is
/// considered at all, however much better its bm25. PRD §08 promises add-in APIs are "ranked below core,
/// never suppressed"; the preference belongs in <c>CoreBoost</c>, which adds +0.5 to a core row's score
/// after selection and suppresses nothing.</para>
///
/// <para>It matters more since issue #91, because the connector's own script API is now indexed as an
/// add-in, so an agent searching it by description takes exactly this path.</para>
///
/// <para><b>Measured before and after</b>, at the production candidate limit of 500, with RevitAPIUI
/// synced as the add-in and the top 50 results inspected:</para>
///
/// <code>
///                                    kind-first   relevance-only
///   "let the user pick an element"       0 add-in     21 add-in
///   "prompt the user"                   12 add-in     12 add-in
///   "show a dialog to the user"         23 add-in     23 add-in
///   "user interface"                    14 add-in     14 add-in
/// </code>
///
/// <para>Only the first is affected, and that is the whole reason this went unnoticed: the exclusion
/// bites only when core alone fills the budget. Most queries never reach it. The one that does is an
/// ordinary phrasing whose right answer (<c>Selection.PickObject</c>) lives entirely in the add-in.</para>
/// </summary>
public class AddInVisibilityTests
{
    /// <summary>Mirrors DiscoveryCache.TierCandidateLimit, which is private.</summary>
    private const int TierCandidateLimit = 500;

    /// <summary>
    /// RevitAPIUI stands in for a third-party add-in: a real, large, separately-loadable assembly with
    /// genuine members and summaries, synced under <c>kind = "addin"</c> so it takes the exact path a real
    /// add-in takes. A hand-built fixture could not work here -- the defect is about candidate VOLUME, so
    /// the corpus has to be the real size.
    /// </summary>
    private static (MetadataLoadContext Context, Assembly Core, Assembly AddIn)? TryLoadPair()
    {
        var loaded = RealRevitApiLoader.TryLoad();
        if (loaded is null)
        {
            return null;
        }

        var uiPath = Path.Combine(Path.GetDirectoryName(loaded.Value.Assembly.Location)!, "RevitAPIUI.dll");
        if (!File.Exists(uiPath))
        {
            loaded.Value.Context.Dispose();
            return null;
        }

        return (loaded.Value.Context, loaded.Value.Assembly, loaded.Value.Context.LoadFromAssemblyPath(uiPath));
    }

    /// <summary>
    /// Counts add-in rows by <c>IsCoreAssembly</c> -- the property actually under test -- rather than by a
    /// namespace prefix, which would only be a proxy for it. Read straight off
    /// <see cref="DiscoveryCache.Search"/>, since <c>MemberSignature</c> (what DiscoveryService returns)
    /// does not carry assembly kind.
    ///
    /// <para>Applies the same ordering DiscoveryService does, so "the first N results" means the same
    /// thing here as it does to an agent. <paramref name="tierThreeOnly"/> restricts the count to rows
    /// scoring below the tier-2 floor of 500, which is what makes a probe a statement about tier 3 rather
    /// than about whatever tier happened to answer.</para>
    /// </summary>
    private static int AddInHits(DiscoveryCache cache, string query, int topN, bool tierThreeOnly = false) =>
        cache.Search(query, namespaceFilter: null)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Member.Name, StringComparer.Ordinal)
            .ThenBy(r => r.Member.MemberId, StringComparer.Ordinal)
            .Take(topN)
            .Count(r => !r.Member.IsCoreAssembly && (!tierThreeOnly || r.Score < 500));

    /// <summary>
    /// How many tier-3 rows the query selected in total. Equal to the candidate budget exactly when the
    /// budget was SATURATED, which is the precondition for this defect: the exclusion can only bite when
    /// selection is competitive.
    ///
    /// <para>A probe that does not establish saturation is not testing the defect. It would keep passing,
    /// silently degraded into a duplicate of the control, if a Revit update, a <c>TierCandidateLimit</c>
    /// change or a <c>TokenizeQuery</c> change meant the query stopped filling the budget -- add-in rows
    /// would then surface for a reason unrelated to the fix.</para>
    ///
    /// <para>Counting CORE rows here would be wrong, and was: after the fix the budget is legitimately
    /// shared (435 core / 65 add-in for the production query), so a core count never equals the budget
    /// again. Saturation is the property that survives the fix; core-fills-it is only observable before.</para>
    /// </summary>
    private static int TierThreeCandidates(DiscoveryCache cache, string query) =>
        cache.Search(query, namespaceFilter: null).Count(r => r.Score < 500);

    /// <summary>
    /// CONTROL. A token the add-in assembly essentially owns, so core cannot crowd it out however the
    /// candidates are ordered. If this ever fails, the fixture is broken and the probes below prove
    /// nothing -- which is the only reason it exists.
    /// </summary>
    [Fact]
    public void AddInRowsAreReachable_WhenCoreDoesNotFillTheBudget()
    {
        var loaded = TryLoadPair();
        if (loaded is null)
        {
            return;
        }

        using var context = loaded.Value.Context;
        using var cache = new DiscoveryCache(":memory:");
        cache.Sync(new[] { ("core", loaded.Value.Core), ("addin", loaded.Value.AddIn) });

        Assert.True(
            AddInHits(cache, "postable command", topN: 20) > 0,
            "the add-in assembly's rows never surfaced even for a token it essentially owns, so the " +
            "probes below would prove nothing");
    }

    /// <summary>
    /// THE PRODUCTION CASE, at the real candidate limit. This is the query that made #81 worth fixing
    /// rather than merely noting: an agent asking how to let a user select something, whose answer
    /// (<c>Autodesk.Revit.UI.Selection.Selection.PickObject</c>) is add-in-side, got back 50 results with
    /// nothing from the add-in in them.
    /// </summary>
    [Fact]
    public void AddInRowsSurvive_WhenCoreAloneFillsTheCandidateBudget()
    {
        var loaded = TryLoadPair();
        if (loaded is null)
        {
            return;
        }

        using var context = loaded.Value.Context;
        using var cache = new DiscoveryCache(":memory:");
        cache.Sync(new[] { ("core", loaded.Value.Core), ("addin", loaded.Value.AddIn) });

        // The PRECONDITION first. Without it this test would keep passing while quietly ceasing to test
        // anything: if a Revit update, a TierCandidateLimit change or a TokenizeQuery change meant core no
        // longer filled the budget for this query, add-in rows would surface for a reason that has nothing
        // to do with the fix, and the assertion below would still be green.
        Assert.Equal(TierCandidateLimit, TierThreeCandidates(cache, "let the user pick an element"));

        Assert.True(
            AddInHits(cache, "let the user pick an element", topN: 50, tierThreeOnly: true) > 0,
            "no add-in row appears in the top 50 for a query whose answer is add-in-side. Core filled the " +
            "tier-3 candidate budget and add-in rows were dropped before ranking saw them (issue #81).");
    }

    /// <summary>
    /// The same defect under a deliberately small candidate budget, which makes the mechanism explicit
    /// rather than dependent on the real corpus happening to be large enough.
    ///
    /// <para>Worth having alongside the production case because it isolates the cause: with a budget of 20
    /// and a token core matches more than 20 times, kind-first ordering spends the entire budget on core.
    /// The production case proves it matters; this one proves why.</para>
    /// </summary>
    [Fact]
    public void AddInRowsSurviveASmallCandidateBudget()
    {
        var loaded = TryLoadPair();
        if (loaded is null)
        {
            return;
        }

        using var context = loaded.Value.Context;
        const int budget = 20;
        using var cache = new DiscoveryCache(":memory:", budget);
        cache.Sync(new[] { ("core", loaded.Value.Core), ("addin", loaded.Value.AddIn) });

        Assert.Equal(budget, TierThreeCandidates(cache, "prompt the user"));

        // tierThreeOnly is load-bearing here, not decoration. _rankedDepth caps BOTH tiers, and
        // "prompt the user" matches add-in member names directly (UIDocument.PromptToPlaceViewOnSheet and
        // RevitAPIUI's other Prompt* members), so an add-in row can arrive via TIER 2 at a score above 500
        // without tier 3 being involved at all. Counting any add-in hit would therefore pass with the fix
        // fully reverted -- which is exactly what this test claims to rule out.
        Assert.True(
            AddInHits(cache, "prompt the user", topN: budget, tierThreeOnly: true) > 0,
            "with a 20-candidate budget and a token core matches more than 20 times, every add-in row was " +
            "dropped at selection time (issue #81).");
    }
}
