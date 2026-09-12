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
}
