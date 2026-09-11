using System.Linq;
using Rhino.MCPBridge.Core.Discovery;
using Xunit;

namespace Rhino.MCPBridge.Discovery.Tests;

/// <summary>
/// Discovery over the REAL RhinoCommon assembly (reflected directly — pure-managed NuGet, no
/// MetadataLoadContext), the Rhino counterpart of the Revit connector's RealRevitApiTests. Asserts only
/// clearly-correct expectations against Rhino's own API so the suite does not overfit the ranker.
/// </summary>
public class RealRhinoCommonTests
{
    private static DiscoveryService Service() => new(RealRhinoCorpus.Shared);

    [Fact]
    public void TheCorpusIsSubstantial()
    {
        var dump = Service().DumpMembers(offset: 0, limit: 1);
        // RhinoCommon documents many thousands of members; a tiny number means reflection/XML broke.
        Assert.True(dump.Total > 5000, $"expected a large RhinoCommon corpus, got {dump.Total} members");
    }

    [Fact]
    public void ListFunctions_NoArgs_SurfacesRhinoNamespaces()
    {
        var result = Service().ListFunctions(namespaceFilter: null, typeFilter: null, cursor: null, pageSize: 500);
        Assert.Contains("Rhino.Geometry", result.Names);
        Assert.Contains("Rhino.DocObjects", result.Names);
    }

    [Fact]
    public void ListFunctions_InRhinoGeometry_ListsRealTypes()
    {
        var result = Service().ListFunctions(namespaceFilter: "Rhino.Geometry", typeFilter: null, cursor: null, pageSize: 500);
        foreach (var t in new[] { "Sphere", "Mesh", "Circle", "Point3d" })
        {
            Assert.Contains(result.Names, n => n == t || n.EndsWith("." + t, System.StringComparison.Ordinal));
        }
    }

    [Fact]
    public void DescribeFunction_ResolvesARealMember_WithADocSummary()
    {
        // DescribeFunction is member-oriented: it splits the trailing segment as the member name, so a
        // documented member (Sphere.Radius) is the right shape, and its summary is the XML-sidecar signal.
        var result = Service().DescribeFunction("Rhino.Geometry.Sphere.Radius", memberId: null);
        Assert.NotNull(result.Single);
        Assert.Equal("Rhino.Geometry.Sphere", result.Single!.DeclaringType);
        Assert.Equal("Radius", result.Single.Name);
        // RhinoCommon.xml must resolve beside the assembly or summaries are null (PRD §09). Radius is
        // documented, so a populated summary is the sidecar-resolved signal.
        Assert.False(string.IsNullOrWhiteSpace(result.Single.Summary),
            "Rhino.Geometry.Sphere.Radius has no summary — RhinoCommon.xml did not resolve beside the assembly");
    }

    [Fact]
    public void DescribeFunction_ResolvesARealMember()
    {
        // Mesh.CreateFromBox is overloaded, so this exercises the overload-list path too.
        var result = Service().DescribeFunction("Rhino.Geometry.Mesh.CreateFromBox", memberId: null);
        Assert.True(result.Single is not null || result.Overloads is not null,
            "Mesh.CreateFromBox resolved to neither a single member nor an overload list");
        if (result.Overloads is not null)
        {
            Assert.NotEmpty(result.Overloads.Overloads);
        }
    }

    [Theory]
    [InlineData("create a mesh from a box", "Mesh")]
    [InlineData("add a circle to the document", "Circle")]
    [InlineData("compute the area of a surface", "Area")]
    public void SearchFunctions_TaskPhrase_FindsRelevantMembers(string query, string expectedToken)
    {
        var result = Service().SearchFunctions(query, namespaceFilter: null, cursor: null, topN: 20);
        Assert.True(result.Results.Count > 0, $"'{query}' matched nothing");
        Assert.Contains(result.Results, r =>
            r.Member.Name.Contains(expectedToken, System.StringComparison.OrdinalIgnoreCase) ||
            r.Member.DeclaringType.Contains(expectedToken, System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SearchFunctions_NamespaceScoped_StaysInNamespace()
    {
        var result = Service().SearchFunctions("mesh", namespaceFilter: "Rhino.Geometry", cursor: null, topN: 20);
        Assert.True(result.Results.Count > 0);
        Assert.All(result.Results, r => Assert.Equal("Rhino.Geometry", r.Member.Namespace));
    }
}
