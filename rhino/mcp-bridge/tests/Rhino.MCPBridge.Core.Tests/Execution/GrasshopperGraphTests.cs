using System.Collections.Generic;
using System.Linq;
using Rhino.MCPBridge.Core.Execution;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Execution;

/// <summary>The pure neighbourhood walk behind frame_canvas (PRD §11): seeds plus up/downstream to a depth,
/// with each direction independent so overlapping sets and cycles resolve correctly.</summary>
public sealed class GrasshopperGraphTests
{
    // Builds adjacency from directed "a->b" edges (a feeds b): b gets a as an Up (source), a gets b as a Down.
    private static Dictionary<string, GrasshopperGraph.Edges> Graph(params (string From, string To)[] edges)
    {
        var up = new Dictionary<string, List<string>>();
        var down = new Dictionary<string, List<string>>();
        var nodes = new HashSet<string>();
        foreach (var (from, to) in edges)
        {
            nodes.Add(from); nodes.Add(to);
            (down.TryGetValue(from, out var d) ? d : down[from] = new()).Add(to);
            (up.TryGetValue(to, out var u) ? u : up[to] = new()).Add(from);
        }

        return nodes.ToDictionary(n => n, n => new GrasshopperGraph.Edges(
            up.TryGetValue(n, out var u) ? u.ToArray() : System.Array.Empty<string>(),
            down.TryGetValue(n, out var d) ? d.ToArray() : System.Array.Empty<string>()));
    }

    [Fact]
    public void DepthZero_IsJustTheSeeds()
    {
        var g = Graph(("A", "B"), ("B", "C"));
        Assert.Equal(new[] { "B" }, GrasshopperGraph.Neighborhood(new[] { "B" }, g, 0, 0).OrderBy(x => x));
    }

    [Fact]
    public void Upstream_And_Downstream_FollowTheRightEdges_ToDepth()
    {
        var g = Graph(("A", "B"), ("B", "C"), ("C", "D")); // A->B->C->D
        Assert.Equal(new[] { "A", "B" }, GrasshopperGraph.Neighborhood(new[] { "B" }, g, 1, 0).OrderBy(x => x));
        Assert.Equal(new[] { "B", "C" }, GrasshopperGraph.Neighborhood(new[] { "B" }, g, 0, 1).OrderBy(x => x));
        Assert.Equal(new[] { "A", "B", "C" }, GrasshopperGraph.Neighborhood(new[] { "B" }, g, 1, 1).OrderBy(x => x));
        Assert.Equal(new[] { "A", "B", "C", "D" }, GrasshopperGraph.Neighborhood(new[] { "B" }, g, 2, 2).OrderBy(x => x));
    }

    [Fact]
    public void OverlappingDirections_DoNotTruncateEachOther()
    {
        // S2 -> X -> {S1, Z}. Seeds {S1, S2}. Upstream-from-S1 claims X; the downstream-from-S2 walk must still
        // descend PAST X to reach Z. A shared visited set (the bug) would stop at X and drop Z.
        var g = Graph(("S2", "X"), ("X", "S1"), ("X", "Z"));
        var hood = GrasshopperGraph.Neighborhood(new[] { "S1", "S2" }, g, upstreamDepth: 1, downstreamDepth: 2);
        Assert.Equal(new[] { "S1", "S2", "X", "Z" }, hood.OrderBy(x => x));
    }

    [Fact]
    public void Cycles_Terminate_AndAreFullyCovered()
    {
        var g = Graph(("A", "B"), ("B", "A")); // a 2-node feedback loop
        var hood = GrasshopperGraph.Neighborhood(new[] { "A" }, g, upstreamDepth: 5, downstreamDepth: 5);
        Assert.Equal(new[] { "A", "B" }, hood.OrderBy(x => x));
    }

    [Fact]
    public void UnknownSeedAndDanglingEdges_AreHandled()
    {
        var g = Graph(("A", "B"));
        // A seed with no adjacency entry is returned as itself; no throw.
        Assert.Equal(new[] { "ghost" }, GrasshopperGraph.Neighborhood(new[] { "ghost" }, g, 3, 3).OrderBy(x => x));
    }
}
