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
///   "let the user pick an element"       0 UI        21 UI
///   "prompt the user"                   12 UI        12 UI
///   "show a dialog to the user"         23 UI        23 UI
/// </code>
///
/// <para>Only the first is affected, and that is the whole reason this went unnoticed: the exclusion
/// bites only when core alone fills the budget. Most queries never reach it. The one that does is an
/// ordinary phrasing whose right answer (<c>Selection.PickObject</c>) lives entirely in the add-in.</para>
/// </summary>
public class AddInVisibilityTests
{
    private const string AddInNamespacePrefix = "Autodesk.Revit.UI";

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

    private static int AddInHits(DiscoveryService service, string query, int topN) =>
        service.SearchFunctions(query, namespaceFilter: null, cursor: null, topN)
            .Results
            .Count(r => r.Member.Namespace.StartsWith(AddInNamespacePrefix, StringComparison.Ordinal));

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

        Assert.True(AddInHits(new DiscoveryService(cache), "postable command", topN: 20) > 0);
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

        var hits = AddInHits(new DiscoveryService(cache), "let the user pick an element", topN: 50);

        Assert.True(
            hits > 0,
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
        using var cache = new DiscoveryCache(":memory:", 20);
        cache.Sync(new[] { ("core", loaded.Value.Core), ("addin", loaded.Value.AddIn) });

        Assert.True(
            AddInHits(new DiscoveryService(cache), "prompt the user", topN: 20) > 0,
            "with a 20-candidate budget and a token core matches more than 20 times, every add-in row was " +
            "dropped at selection time (issue #81).");
    }
}
