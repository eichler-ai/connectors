using System.Collections.Generic;
using System.Linq;
using Rhino.MCPBridge.Core.Discovery;
using Xunit;

namespace Rhino.MCPBridge.Discovery.Tests;

/// <summary>
/// GrasshopperComponentIndexer (PRD §09 kind=grasshopper) + its path through DiscoveryCache/DiscoveryService.
/// The indexer is pure — fed hand-built catalog entries (no live Grasshopper) — so these run on CI, and they
/// prove GH components are searchable, listable and describable by BOTH member and member_id.
/// </summary>
public class GrasshopperComponentIndexerTests
{
    private static GrasshopperCatalogEntry Entry(string guid, string name, string category, string sub,
        string? desc, (string, string)[] inputs, (string, string)[] outputs) =>
        new(guid, name, name.Substring(0, 1), category, sub, desc,
            inputs.Select(i => new GrasshopperPort(i.Item1, i.Item2, null)).ToList(),
            outputs.Select(o => new GrasshopperPort(o.Item1, o.Item2, null)).ToList());

    private static IReadOnlyList<GrasshopperCatalogEntry> Catalog() => new[]
    {
        Entry("11111111-1111-1111-1111-111111111111", "Circle", "Curve", "Primitive", "Create a circle defined by base plane and radius",
            new[] { ("Plane", "Plane"), ("Radius", "Number") }, new[] { ("Circle", "Circle") }),
        Entry("22222222-2222-2222-2222-222222222222", "Line", "Curve", "Primitive", "Create a line between two points",
            new[] { ("Start Point", "Point"), ("End Point", "Point") }, new[] { ("Line", "Line") }),
        Entry("33333333-3333-3333-3333-333333333333", "Number Slider", "Params", "Input", "A numeric slider",
            System.Array.Empty<(string, string)>(), new[] { ("Number", "Number") }),
    };

    [Fact]
    public void Build_GroupsByCategory_AndSynthesizesMembers()
    {
        var (_, types) = GrasshopperComponentIndexer.Build(Catalog())!.Value;
        Assert.All(types, t => Assert.Equal("Grasshopper", t.Namespace));
        var curve = types.Single(t => t.Name == "Curve");
        Assert.Equal("Grasshopper.Curve", curve.FullName);
        var circle = curve.Members.Single(m => m.Name == "Circle");
        Assert.Equal("GrasshopperComponent", circle.Kind);
        Assert.Equal("Grasshopper.Curve.Circle", circle.MemberId);
        Assert.Contains("Plane:Plane", circle.Signature);
        Assert.Contains("Circle:Circle", circle.Signature);
        // The python_call is a real placement line carrying the component guid.
        Assert.Contains("EmitObject(System.Guid(\"11111111-1111-1111-1111-111111111111\"))", circle.PythonCall);
        Assert.Equal(2, circle.Parameters.Count);
        Assert.Equal("Plane", circle.Parameters[0].Name);
        Assert.Equal("Create a circle defined by base plane and radius", circle.Summary);
    }

    [Fact]
    public void Build_EmptyCatalog_IsNull()
    {
        Assert.Null(GrasshopperComponentIndexer.Build(System.Array.Empty<GrasshopperCatalogEntry>()));
    }

    [Fact]
    public void Build_DeduplicatesSameNameWithinACategory()
    {
        var dupes = new[]
        {
            Entry("aaaa1111-1111-1111-1111-111111111111", "Circle", "Curve", "Primitive", "one", System.Array.Empty<(string, string)>(), System.Array.Empty<(string, string)>()),
            Entry("bbbb2222-2222-2222-2222-222222222222", "Circle", "Curve", "Primitive", "two", System.Array.Empty<(string, string)>(), System.Array.Empty<(string, string)>()),
        };
        var (_, types) = GrasshopperComponentIndexer.Build(dupes)!.Value;
        Assert.Single(types.Single(t => t.Name == "Curve").Members, m => m.MemberId == "Grasshopper.Curve.Circle");
    }

    [Fact]
    public void ContentHash_IsStableAcrossOrder_AndChangesWithTheSet()
    {
        var a = GrasshopperComponentIndexer.Build(Catalog())!.Value.ContentHash;
        var reordered = Catalog().Reverse().ToList();
        var b = GrasshopperComponentIndexer.Build(reordered)!.Value.ContentHash;
        Assert.Equal(a, b); // order-independent (sorted by guid internally)
        var fewer = Catalog().Take(2).ToList();
        Assert.NotEqual(a, GrasshopperComponentIndexer.Build(fewer)!.Value.ContentHash);
    }

    private static DiscoveryCache SyncedCache()
    {
        var indexed = GrasshopperComponentIndexer.Build(Catalog())!.Value;
        var cache = new DiscoveryCache(":memory:");
        cache.SyncSource("grasshopper", GrasshopperComponentIndexer.SourceId, indexed.ContentHash, indexed.Types);
        return cache;
    }

    [Fact]
    public void SyncSource_MakesComponentsSearchableAndListable()
    {
        var cache = SyncedCache();
        var hit = cache.Search("circle", namespaceFilter: null).FirstOrDefault(s => s.Member.MemberId == "Grasshopper.Curve.Circle");
        Assert.NotNull(hit.Member);
        Assert.Equal("Grasshopper", hit.Member.Namespace);

        Assert.Contains(cache.ListNamespaces(), n => n.Namespace == "Grasshopper");
        var types = cache.ListTypeNames("Grasshopper");
        Assert.Contains("Curve", types);
        Assert.Contains("Params", types);
        Assert.Contains("Circle", cache.ListMemberNames("Grasshopper", "Curve"));
        Assert.Contains("Line", cache.ListMemberNames("Grasshopper", "Curve"));
    }

    [Fact]
    public void DescribeFunction_ResolvesByMember_AndByMemberId()
    {
        var svc = new DiscoveryService(SyncedCache());
        var byMember = svc.DescribeFunction("Grasshopper.Curve.Circle", null);
        Assert.NotNull(byMember.Single);
        Assert.Equal("Create a circle defined by base plane and radius", byMember.Single!.Summary);
        Assert.Contains("EmitObject", byMember.Single.PythonCall);
        Assert.Equal(2, byMember.Single.Parameters.Count);

        // The dotted member_id resolves too (unlike rhinoscript's colon ids) — the search->describe flow.
        var byId = new DiscoveryService(SyncedCache()).DescribeFunction(null, "Grasshopper.Curve.Circle");
        Assert.NotNull(byId.Single);
        Assert.Equal("Circle", byId.Single!.Name);
    }

    [Fact]
    public void SyncSource_IsHashStable_AndSurvivesAssemblySync()
    {
        var indexed = GrasshopperComponentIndexer.Build(Catalog())!.Value;
        using var cache = new DiscoveryCache(":memory:");
        Assert.Equal(1, cache.SyncSource("grasshopper", GrasshopperComponentIndexer.SourceId, indexed.ContentHash, indexed.Types).Added);
        Assert.Equal(1, cache.SyncSource("grasshopper", GrasshopperComponentIndexer.SourceId, indexed.ContentHash, indexed.Types).Unchanged);

        // An assembly sync (which prunes only core/addin) must not delete the grasshopper synthetic source.
        Assert.Equal(0, cache.Sync(System.Array.Empty<(string, System.Reflection.Assembly)>()).Removed);
        Assert.Contains(cache.ListNamespaces(), n => n.Namespace == "Grasshopper");
    }
}
