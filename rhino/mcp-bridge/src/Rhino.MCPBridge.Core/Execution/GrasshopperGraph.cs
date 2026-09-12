namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// The pure graph walk behind frame_canvas's neighbourhood (PRD §11), over an adjacency keyed by object id.
/// Core-side and free of Grasshopper types so it is tier-1 testable; the adapter builds the adjacency from a
/// GH_Document (component-level wiring) and maps the result back to objects.
/// </summary>
internal static class GrasshopperGraph
{
    /// <summary>One node's neighbours: the ids feeding it (<see cref="Up"/>) and the ids it feeds (<see cref="Down"/>).</summary>
    internal sealed class Edges
    {
        public string[] Up { get; }
        public string[] Down { get; }
        public Edges(string[] up, string[] down) { Up = up; Down = down; }
    }

    /// <summary>Every id reachable from <paramref name="seeds"/> within <paramref name="upstreamDepth"/> levels
    /// of Up edges and <paramref name="downstreamDepth"/> levels of Down edges, seeds included. Each direction
    /// walks with its OWN visited set so an id claimed by one direction never blocks the other from descending
    /// past it (they only share the final accumulator); cycles terminate because each direction's visited set
    /// admits every id at most once.</summary>
    internal static HashSet<string> Neighborhood(IReadOnlyCollection<string> seeds,
        IReadOnlyDictionary<string, Edges> adjacency, int upstreamDepth, int downstreamDepth)
    {
        var acc = new HashSet<string>(seeds);
        Walk(acc, seeds, adjacency, upstreamDepth, upstream: true);
        Walk(acc, seeds, adjacency, downstreamDepth, upstream: false);
        return acc;
    }

    private static void Walk(HashSet<string> acc, IReadOnlyCollection<string> seeds,
        IReadOnlyDictionary<string, Edges> adjacency, int depth, bool upstream)
    {
        var visited = new HashSet<string>(seeds);
        var frontier = new List<string>(seeds);
        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            var next = new List<string>();
            foreach (var node in frontier)
            {
                if (!adjacency.TryGetValue(node, out var edges))
                {
                    continue;
                }

                foreach (var neighbour in upstream ? edges.Up : edges.Down)
                {
                    if (visited.Add(neighbour))
                    {
                        next.Add(neighbour);
                        acc.Add(neighbour);
                    }
                }
            }

            frontier = next;
        }
    }
}
