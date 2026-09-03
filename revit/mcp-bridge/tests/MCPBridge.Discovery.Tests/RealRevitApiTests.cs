using System;
using System.Linq;
using MCPBridge.Core.Discovery;
using Xunit;

namespace MCPBridge.Discovery.Tests;

/// <summary>
/// Optional, self-skipping coverage against the real RevitAPI.dll/xml -- meaningless on this Mac dev
/// worktree (no Revit install), free extra confidence on the Windows VM where the real DLL exists. Set
/// MCPBRIDGE_REVITAPI_DLL to the full path of a RevitAPI.dll (with RevitAPI.xml sitting next to it, the
/// normal Revit install layout) to enable; unset (the default everywhere else) skips at runtime rather than
/// failing. See <see cref="RealRevitApiLoader"/> for why the assembly is loaded for metadata only.
/// </summary>
public class RealRevitApiTests
{
    [Fact]
    public void ListFunctions_AgainstRealRevitApiDll_ScopedByDocumentNamespace_ReturnsMembers()
    {
        var loaded = RealRevitApiLoader.TryLoad();
        if (loaded is null)
        {
            return; // Not configured in this environment -- skip rather than fail.
        }

        using var context = loaded.Value.Context;
        using var cache = new DiscoveryCache(":memory:");
        cache.Sync(new[] { ("core", loaded.Value.Assembly) });
        var service = new DiscoveryService(cache);

        var result = service.ListFunctions(namespaceFilter: "Autodesk.Revit.DB", typeFilter: "Document", cursor: null, pageSize: 500);

        Assert.NotEmpty(result.Names);
        Assert.Contains("Delete", result.Names);
    }

    /// <summary>
    /// Issue #65's reported query, against the real corpus it was reported against. The fixture-assembly
    /// tests in DiscoveryCacheTests pin the same behaviour portably, but only this one answers the question
    /// the issue actually asks -- whether an agent with no prior Revit API knowledge, phrasing a task the
    /// way PRD §13's tutorial corpus phrases it, reaches the right method. A hand-built fixture cannot
    /// prove that: the real ranking has to win against hundreds of competing matches, not one.
    /// </summary>
    [Theory]
    // The reported case. "place" belongs to "place a view", but is also a prefix of "Placeholder".
    [InlineData("create sheet place view", "Autodesk.Revit.DB.ViewSheet", "Create")]
    // Issue #75's reported query: Revit's factory convention is NewXxx, not CreateXxx, so this needed
    // synonym expansion rather than any amount of retuning IdentifierRelevance's weights. Measured live
    // (independent review of the first cut of this fix, after fixing the double-counting defect it found):
    // rank 1 at 663.1, ahead of Document.FamilyCreate (616.7) and every ItemFactoryBase.NewFamilyInstance
    // overload (616.5).
    [InlineData("create family instance", "Autodesk.Revit.Creation.Document", "NewFamilyInstance")]
    public void SearchFunctions_NaturalLanguageQuery_SurfacesTheGeneralMethodAtTheTop(string query, string expectedType, string expectedMember)
    {
        var loaded = RealRevitApiLoader.TryLoad();
        if (loaded is null)
        {
            return; // Not configured in this environment -- skip rather than fail.
        }

        using var context = loaded.Value.Context;
        using var cache = new DiscoveryCache(":memory:");
        cache.Sync(new[] { ("core", loaded.Value.Assembly) });
        var service = new DiscoveryService(cache);

        var result = service.SearchFunctions(query, namespaceFilter: null, cursor: null, topN: 20);

        var hit = result.Results.FirstOrDefault(r =>
            r.Member.DeclaringType == expectedType && r.Member.Name == expectedMember);

        // The issue's actual complaint was absence, not mere position: the right method appeared NOWHERE in
        // 20 results, leaving an agent with no next query to try. Assert presence first so a failure says
        // which of the two problems came back.
        Assert.True(
            hit is not null,
            $"'{expectedType}.{expectedMember}' is absent from the top 20 for \"{query}\"; got: "
                + string.Join(", ", result.Results.Take(5).Select(r => $"{r.Member.DeclaringType}.{r.Member.Name} ({r.Score:F1})")));

        var top = result.Results[0];
        Assert.True(
            top.Member.DeclaringType == expectedType && top.Member.Name == expectedMember,
            $"expected '{expectedType}.{expectedMember}' at rank 1 for \"{query}\", got "
                + $"'{top.Member.DeclaringType}.{top.Member.Name}' ({top.Score:F1}) with the expected member at score {hit!.Score:F1}");
    }

    /// <summary>
    /// Issue #186 against the corpus it was reported on. NamedIndexedPropertyTests pins the mechanism
    /// portably on a VB fixture; this pins the three real members the fix exists for -- the reported
    /// FootPrintRoof.SlopeAngle, the most-called named indexed property in the API (Element.Parameter), an
    /// `Item` property that is named rather than default (ModelCurveArray), and a C++/CLI DEFAULT indexed
    /// property (UV's) that must keep the `this[...]` form.
    /// </summary>
    [Fact]
    public void NamedIndexedProperties_AgainstRealRevitApiDll_RenderAndResolveAsAccessors()
    {
        var loaded = RealRevitApiLoader.TryLoad();
        if (loaded is null)
        {
            return; // Not configured in this environment -- skip rather than fail.
        }

        using var context = loaded.Value.Context;
        using var cache = new DiscoveryCache(":memory:");
        cache.Sync(new[] { ("core", loaded.Value.Assembly) });
        var service = new DiscoveryService(cache);

        var slope = service.DescribeFunction("Autodesk.Revit.DB.FootPrintRoof.SlopeAngle", memberId: null);
        Assert.Equal(
            "double get_SlopeAngle(ModelCurve pCurve); void set_SlopeAngle(ModelCurve pCurve, double value)",
            slope.Single!.Signature);

        var setter = service.DescribeFunction("Autodesk.Revit.DB.FootPrintRoof.set_SlopeAngle", memberId: null);
        Assert.Equal(slope.Single.MemberId, setter.Single!.MemberId);

        var parameter = service.DescribeFunction("Autodesk.Revit.DB.Element.Parameter", memberId: null);
        Assert.NotNull(parameter.Overloads);
        Assert.Equal(3, parameter.Overloads!.Overloads.Count);
        Assert.All(parameter.Overloads.Overloads, o => Assert.StartsWith("Parameter get_Parameter(", o.Signature));

        // UV declares a C++/CLI `default` indexed property, the one shape that carries DefaultMemberAttribute
        // and so keeps the `this[...]` form.
        var uvIndexer = cache.GetMembersIncludingInheritedByFullName("Autodesk.Revit.DB.UV")
            .Single(m => m.Kind == "Property" && m.Parameters.Count > 0);
        Assert.Contains("this[", uvIndexer.Signature);

        // The 40 `Item(...)` properties (ModelCurveArray, PhaseArray, ParameterMap, ...) are NOT default members:
        // no DefaultMemberAttribute, so C# reaches them as `get_Item(i)` -- the spelling Revit C# code has
        // always had to use. Measured here rather than assumed: the first cut of this test expected `this[`
        // for ModelCurveArray and the real DLL said otherwise.
        var itemIndexer = cache.GetMembersIncludingInheritedByFullName("Autodesk.Revit.DB.ModelCurveArray")
            .Single(m => m.Kind == "Property" && m.Parameters.Count > 0);
        Assert.StartsWith("ModelCurve get_Item(int ", itemIndexer.Signature);
    }

    /// <summary>
    /// A conversationally-phrased task, carrying several English function words no API name contains.
    ///
    /// <para>Asserts CONTENTION, not rank 1, and the distinction is deliberate. "create a wall on a level"
    /// names two types the corpus can build -- Wall and Level -- and nothing in a token-overlap ranker can
    /// know that "wall" is the object being created while "level" is a modifier, so Wall.Create and
    /// Level.Create score identically by construction. Demanding a specific winner would be asserting
    /// something the ranker cannot principledly decide, and would pin whichever way a tie-break happened to
    /// fall. What the fix genuinely guarantees, and what the issue is actually about, is that the right
    /// method stays reachable rather than dropping below the tier-3 ceiling where no amount of paging finds
    /// it.</para>
    ///
    /// <para>Found live while writing this test: before stopword filtering, "a" and "on" matched
    /// "WallFound<b>a</b>ti<b>on</b>" as substrings and nothing in "Wall", which both ranked
    /// WallFoundation.Create above Wall.Create AND pushed Wall.Create out of tier 2 altogether (460.2,
    /// i.e. below the 500 floor).</para>
    /// </summary>
    [Fact]
    public void SearchFunctions_ConversationalQuery_KeepsTheRightMethodInContention()
    {
        var loaded = RealRevitApiLoader.TryLoad();
        if (loaded is null)
        {
            return; // Not configured in this environment -- skip rather than fail.
        }

        using var context = loaded.Value.Context;
        using var cache = new DiscoveryCache(":memory:");
        cache.Sync(new[] { ("core", loaded.Value.Assembly) });
        var service = new DiscoveryService(cache);

        var result = service.SearchFunctions("create a wall on a level", namespaceFilter: null, cursor: null, topN: 20);

        var hit = result.Results.FirstOrDefault(r =>
            r.Member.DeclaringType == "Autodesk.Revit.DB.Wall" && r.Member.Name == "Create");

        Assert.True(
            hit is not null,
            "'Autodesk.Revit.DB.Wall.Create' is absent from the top 20; got: "
                + string.Join(", ", result.Results.Take(5).Select(r => $"{r.Member.DeclaringType}.{r.Member.Name} ({r.Score:F1})")));

        // Above the tier-2 floor: tier 3 is bounded below 500, so a score under it means the stray function
        // words dropped this method a whole tier again and no better match could ever be outranked.
        Assert.InRange(hit!.Score, 500, 999);
    }

    /// <summary>
    /// Issue #76, at the scale it actually occurs. The fixture test in DiscoveryCacheTests pins the
    /// mechanism portably by shrinking the cap to 2; only the real corpus has the thousands of candidates
    /// that make the shipped cap of 500 bite at all.
    ///
    /// <para>A SHORT query on a common word is the shape that reaches it -- not the long natural-language
    /// phrasing the issue was filed against, which turned out never to come close (455 and 592 candidates
    /// against a 500 cap, where the issue claimed ~795 and ~802; those figures came from a proxy corpus
    /// built from RevitAPI.xml, not from this code path). With one token, admission needs a single hit and
    /// a declaring-type name supplies it, so "id" admits 4,768 rows and "get" 2,413.</para>
    ///
    /// <para>Both members below were measured ABSENT from the entire ranked result under the old
    /// scan-order cut, despite being among the highest-scoring rows in the corpus for their query:
    /// Entity.Get scores 637.5 while the old rank 1 scored 600.1, and five rows tied with Element.Id at
    /// 637.5 were dropped while Element.Id itself survived. That is the issue's first consequence -- a
    /// correct answer vanishing, with no cursor walk able to reach it -- so presence is what is asserted,
    /// not a position that a later retune could legitimately move.</para>
    /// </summary>
    [Theory]
    [InlineData("get", "Autodesk.Revit.DB.ExtensibleStorage.Entity", "Get")]
    [InlineData("id", "Autodesk.Revit.DB.Category", "Id")]
    public void SearchFunctions_WideSingleTokenQuery_StillReachesTheHighestScoringMembers(string query, string expectedType, string expectedMember)
    {
        var loaded = RealRevitApiLoader.TryLoad();
        if (loaded is null)
        {
            return; // Not configured in this environment -- skip rather than fail.
        }

        using var context = loaded.Value.Context;
        using var cache = new DiscoveryCache(":memory:");
        cache.Sync(new[] { ("core", loaded.Value.Assembly) });
        var service = new DiscoveryService(cache);

        var result = service.SearchFunctions(query, namespaceFilter: null, cursor: null, topN: 20);

        var hit = result.Results.FirstOrDefault(r =>
            r.Member.DeclaringType == expectedType && r.Member.Name == expectedMember);

        Assert.True(
            hit is not null,
            $"'{expectedType}.{expectedMember}' is absent from the top 20 for \"{query}\"; got: "
                + string.Join(", ", result.Results.Take(5).Select(r => $"{r.Member.DeclaringType}.{r.Member.Name} ({r.Score:F1})")));

        // Nothing on the page may outscore it: the defect was not that it ranked low but that better-scoring
        // rows were being cut before ranking, which shows up here as a survivor scoring above the best row
        // the cap let through.
        Assert.Equal(result.Results.Max(r => r.Score), hit!.Score, precision: 9);
    }
}
