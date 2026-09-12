using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Rhino.MCPBridge.Core.Execution;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// Reads the structure of a Grasshopper definition for inspect_gh_definition (PRD §10): the objects on the
/// canvas, their canvas positions, and their component-level wiring. Grasshopper-typed, so it is only
/// reached once Grasshopper is loaded (the caller guards with <see cref="GrasshopperWatcher.GrasshopperLoaded"/>)
/// and runs on the main thread. The wiring helpers here are shared with the canvas-framing work (PRD §11).
/// </summary>
internal static class GrasshopperInspector
{
    public static GrasshopperDefinitionInfo Inspect(GH_Document doc, string grasshopperDocumentId, string? nameFilter, int offset, int limit)
    {
        var title = string.IsNullOrEmpty(doc.DisplayName) ? "Untitled" : doc.DisplayName;
        var path = string.IsNullOrEmpty(doc.FilePath) ? null : doc.FilePath;

        // The set of instance guids actually in this definition, so a wiring neighbour that lives in another
        // document (a cluster's inner params) is not reported as an edge to a guid no returned object has.
        var docGuids = new HashSet<string>();
        foreach (var o in doc.Objects)
        {
            if (o is null) continue;
            try { docGuids.Add(o.InstanceGuid.ToString()); } catch { /* a guid that cannot be read is not an edge target */ }
        }

        var objects = new List<GrasshopperObjectInfo>();
        var matchIndex = 0;
        foreach (var obj in doc.Objects)
        {
            if (obj is null)
            {
                continue;
            }

            // A misbehaving third-party object can throw from any getter (nickname, attributes, port lists).
            // inspect_gh_definition is a diagnostic tool, so degrade that one object to a placeholder rather than
            // abort the whole read — mirroring the Safe* guards in GrasshopperOperations' Get/Data.
            bool matched;
            try { matched = Matches(obj, nameFilter); }
            catch { matched = true; } // can't evaluate the filter on a throwing object — surface it, don't hide it

            if (!matched)
            {
                continue;
            }

            if (matchIndex >= offset && objects.Count < limit)
            {
                try { objects.Add(Describe(obj, docGuids)); }
                catch (Exception ex) { objects.Add(Degraded(obj, ex)); }
            }

            matchIndex++;
        }

        var truncated = matchIndex > offset + objects.Count;
        return new GrasshopperDefinitionInfo(grasshopperDocumentId, title, path, doc.ObjectCount, doc.Enabled,
            matchIndex, offset, truncated, objects);
    }

    private static bool Matches(IGH_DocumentObject obj, string? nameFilter)
    {
        if (string.IsNullOrEmpty(nameFilter))
        {
            return true;
        }

        return Contains(obj.NickName, nameFilter) || Contains(obj.Name, nameFilter);
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static GrasshopperObjectInfo Describe(IGH_DocumentObject obj, HashSet<string> docGuids)
    {
        var attr = obj.Attributes;
        var pivot = attr is null ? new double[] { 0, 0 } : new double[] { attr.Pivot.X, attr.Pivot.Y };
        var bounds = attr is null
            ? new double[] { 0, 0, 0, 0 }
            : new double[] { attr.Bounds.X, attr.Bounds.Y, attr.Bounds.Width, attr.Bounds.Height };

        var (upstream, downstream) = Wiring(obj, docGuids);
        var nickname = string.IsNullOrEmpty(obj.NickName) ? (obj.Name ?? "") : obj.NickName;
        return new GrasshopperObjectInfo(obj.InstanceGuid.ToString(), nickname, obj.Name ?? obj.GetType().Name,
            KindOf(obj), pivot, bounds, upstream, downstream);
    }

    /// <summary>A placeholder for an object whose properties threw: its guid if readable, a marker name, no
    /// position, no wiring — so the agent sees the object is there (and that it is broken) without the whole
    /// inspection failing.</summary>
    private static GrasshopperObjectInfo Degraded(IGH_DocumentObject obj, Exception ex)
    {
        string guid;
        try { guid = obj.InstanceGuid.ToString(); } catch { guid = Guid.Empty.ToString(); }
        return new GrasshopperObjectInfo(guid, "", $"(unavailable: {ex.GetType().Name})", "other",
            new double[] { 0, 0 }, new double[] { 0, 0, 0, 0 }, Array.Empty<string>(), Array.Empty<string>());
    }

    private static string KindOf(IGH_DocumentObject obj) => obj switch
    {
        IGH_Component => "component",
        IGH_Param => "param",
        _ => "other",
    };

    /// <summary>The component-level wiring of one object: the distinct instance guids of the objects that
    /// feed it (upstream) and that it feeds (downstream), restricted to objects in this definition. A
    /// component's ports are its input/output parameters; a standalone parameter wires directly. Each
    /// source/recipient parameter is resolved to its owning top-level object so the graph is object-to-object,
    /// which is what a depth walk needs. Returned sorted for a stable result.</summary>
    internal static (string[] Upstream, string[] Downstream) Wiring(IGH_DocumentObject obj, HashSet<string> docGuids)
    {
        var self = obj.InstanceGuid;
        var upstream = new SortedSet<string>(StringComparer.Ordinal);
        var downstream = new SortedSet<string>(StringComparer.Ordinal);

        switch (obj)
        {
            case IGH_Component component:
                foreach (var input in component.Params.Input)
                {
                    AddOwners(upstream, input.Sources, self, docGuids);
                }

                foreach (var output in component.Params.Output)
                {
                    AddOwners(downstream, output.Recipients, self, docGuids);
                }

                break;
            case IGH_Param param:
                AddOwners(upstream, param.Sources, self, docGuids);
                AddOwners(downstream, param.Recipients, self, docGuids);
                break;
        }

        return (upstream.ToArray(), downstream.ToArray());
    }

    private static void AddOwners(SortedSet<string> into, IEnumerable<IGH_Param>? parameters, Guid self, HashSet<string> docGuids)
    {
        if (parameters is null)
        {
            return;
        }

        foreach (var p in parameters)
        {
            var owner = p?.Attributes?.GetTopLevel?.DocObject;
            if (owner is null || owner.InstanceGuid == self)
            {
                continue;
            }

            var guid = owner.InstanceGuid.ToString();
            if (docGuids.Contains(guid)) // an edge into/out of another document (a cluster's internals) is not reported
            {
                into.Add(guid);
            }
        }
    }

    // ---- canvas framing support (PRD §11, frame_canvas) ----

    /// <summary>Resolves each requested nickname/guid to an object on the canvas (first match wins, as
    /// Find/Set do), reporting which requests matched nothing so the caller can surface them.</summary>
    internal static (List<IGH_DocumentObject> Matched, List<string> Missing) FindObjects(GH_Document doc, IReadOnlyList<string> nicknamesOrGuids)
    {
        var matched = new List<IGH_DocumentObject>();
        var missing = new List<string>();
        foreach (var q in nicknamesOrGuids)
        {
            var obj = FindOne(doc, q);
            if (obj is null)
            {
                missing.Add(q);
            }
            else
            {
                matched.Add(obj);
            }
        }

        return (matched, missing);
    }

    private static IGH_DocumentObject? FindOne(GH_Document doc, string nicknameOrGuid)
    {
        var byGuid = Guid.TryParse(nicknameOrGuid, out var g);
        foreach (var obj in doc.Objects)
        {
            if (obj is null) continue;
            var nick = string.IsNullOrEmpty(obj.NickName) ? (obj.Name ?? "") : obj.NickName;
            if (byGuid ? obj.InstanceGuid == g : string.Equals(nick, nicknameOrGuid, StringComparison.OrdinalIgnoreCase))
            {
                return obj;
            }
        }

        return null;
    }

    /// <summary>Every object reachable from <paramref name="seeds"/> within <paramref name="upstreamDepth"/>
    /// levels of sources and <paramref name="downstreamDepth"/> levels of recipients, seeds included. Builds the
    /// component-level adjacency (the same <see cref="Wiring"/> as inspect_gh_definition) and delegates the walk
    /// to the pure, tier-1-tested <see cref="GrasshopperGraph"/>.</summary>
    internal static HashSet<IGH_DocumentObject> Neighborhood(GH_Document doc, IReadOnlyList<IGH_DocumentObject> seeds, int upstreamDepth, int downstreamDepth)
    {
        var byGuid = new Dictionary<Guid, IGH_DocumentObject>();
        var docGuids = new HashSet<string>();
        foreach (var o in doc.Objects)
        {
            if (o is null) continue;
            byGuid[o.InstanceGuid] = o;
            docGuids.Add(o.InstanceGuid.ToString());
        }

        var adjacency = new Dictionary<string, GrasshopperGraph.Edges>();
        foreach (var o in doc.Objects)
        {
            if (o is null) continue;
            var (up, down) = Wiring(o, docGuids);
            adjacency[o.InstanceGuid.ToString()] = new GrasshopperGraph.Edges(up, down);
        }

        var seedGuids = new List<string>(seeds.Count);
        foreach (var s in seeds)
        {
            if (s is not null) seedGuids.Add(s.InstanceGuid.ToString());
        }

        var hood = GrasshopperGraph.Neighborhood(seedGuids, adjacency, upstreamDepth, downstreamDepth);
        var result = new HashSet<IGH_DocumentObject>();
        foreach (var guid in hood)
        {
            if (Guid.TryParse(guid, out var g) && byGuid.TryGetValue(g, out var obj))
            {
                result.Add(obj);
            }
        }

        return result;
    }

    /// <summary>The union of the canvas bounds of a set of objects, or an empty rectangle when none have
    /// bounds. Canvas coordinates.</summary>
    internal static System.Drawing.RectangleF UnionBounds(IEnumerable<IGH_DocumentObject> objects)
    {
        System.Drawing.RectangleF? acc = null;
        foreach (var o in objects)
        {
            var attr = o?.Attributes;
            if (attr is null) continue;
            var b = attr.Bounds;
            acc = acc is null ? b : System.Drawing.RectangleF.Union(acc.Value, b);
        }

        return acc ?? System.Drawing.RectangleF.Empty;
    }
}
