using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Rhino.MCPBridge.Core.Execution;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// Reads the structure of a Grasshopper definition for inspect_definition (PRD §10): the objects on the
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

        var objects = new List<GrasshopperObjectInfo>();
        var matchIndex = 0;
        foreach (var obj in doc.Objects)
        {
            if (obj is null || !Matches(obj, nameFilter))
            {
                continue;
            }

            if (matchIndex >= offset && objects.Count < limit)
            {
                objects.Add(Describe(obj));
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

    private static GrasshopperObjectInfo Describe(IGH_DocumentObject obj)
    {
        var attr = obj.Attributes;
        var pivot = attr is null ? new double[] { 0, 0 } : new double[] { attr.Pivot.X, attr.Pivot.Y };
        var bounds = attr is null
            ? new double[] { 0, 0, 0, 0 }
            : new double[] { attr.Bounds.X, attr.Bounds.Y, attr.Bounds.Width, attr.Bounds.Height };

        var (upstream, downstream) = Wiring(obj);
        var nickname = string.IsNullOrEmpty(obj.NickName) ? (obj.Name ?? "") : obj.NickName;
        return new GrasshopperObjectInfo(obj.InstanceGuid.ToString(), nickname, obj.Name ?? obj.GetType().Name,
            KindOf(obj), pivot, bounds, upstream, downstream);
    }

    private static string KindOf(IGH_DocumentObject obj) => obj switch
    {
        IGH_Component => "component",
        IGH_Param => "param",
        _ => "other",
    };

    /// <summary>The component-level wiring of one object: the distinct instance guids of the objects that
    /// feed it (upstream) and that it feeds (downstream). A component's ports are its input/output
    /// parameters; a standalone parameter wires directly. Each source/recipient parameter is resolved to
    /// its owning top-level object so the graph is object-to-object, which is what a depth walk needs.</summary>
    internal static (string[] Upstream, string[] Downstream) Wiring(IGH_DocumentObject obj)
    {
        var self = obj.InstanceGuid;
        var upstream = new HashSet<string>();
        var downstream = new HashSet<string>();

        switch (obj)
        {
            case IGH_Component component:
                foreach (var input in component.Params.Input)
                {
                    AddOwners(upstream, input.Sources, self);
                }

                foreach (var output in component.Params.Output)
                {
                    AddOwners(downstream, output.Recipients, self);
                }

                break;
            case IGH_Param param:
                AddOwners(upstream, param.Sources, self);
                AddOwners(downstream, param.Recipients, self);
                break;
        }

        return (upstream.ToArray(), downstream.ToArray());
    }

    private static void AddOwners(HashSet<string> into, IEnumerable<IGH_Param>? parameters, Guid self)
    {
        if (parameters is null)
        {
            return;
        }

        foreach (var p in parameters)
        {
            var owner = p?.Attributes?.GetTopLevel?.DocObject;
            if (owner is not null && owner.InstanceGuid != self)
            {
                into.Add(owner.InstanceGuid.ToString());
            }
        }
    }
}
