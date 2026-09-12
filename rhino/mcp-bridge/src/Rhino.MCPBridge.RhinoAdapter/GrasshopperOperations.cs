using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Eichler.Connectors.Rhino;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Rhino.MCPBridge.Core.Execution;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// The real <see cref="IGrasshopperOperations"/> (PRD §10): drives a bound <c>GH_Document</c> from a
/// script's <c>Connector.Grasshopper</c>. Grasshopper-typed, so it is only reached from inside a run where
/// a definition was resolved (Grasshopper is loaded). Every method runs on the main thread.
/// </summary>
internal sealed class GrasshopperOperations : IGrasshopperOperations
{
    public GrasshopperComponent? Find(object grasshopperDocument, string nicknameOrGuid)
    {
        var obj = FindObject((GH_Document)grasshopperDocument, nicknameOrGuid);
        return obj is null ? null : Describe(obj);
    }

    public void Set(object grasshopperDocument, string nickname, object value)
    {
        var obj = FindObject((GH_Document)grasshopperDocument, nickname)
            ?? throw new InvalidOperationException($"no Grasshopper object with nickname or id '{nickname}' is on the canvas.");

        switch (obj)
        {
            case GH_NumberSlider slider:
                slider.SetSliderValue(ToDecimal(value));
                break;
            case GH_BooleanToggle toggle:
                toggle.Value = ToBool(value);
                break;
            case GH_Panel panel:
                panel.UserText = value?.ToString() ?? "";
                break;
            case GH_ValueList list:
                SelectValueListItem(list, value);
                break;
            default:
                throw new InvalidOperationException($"'{Nick(obj)}' is a {obj.Name} — Set handles a Number Slider, Boolean Toggle, Panel or Value List; to feed other inputs use Reference (document geometry) or drive the GH_Document directly.");
        }

        obj.ExpireSolution(recompute: false);
    }

    public void Reference(object grasshopperDocument, string nickname, object? objectIds)
    {
        var obj = FindObject((GH_Document)grasshopperDocument, nickname)
            ?? throw new InvalidOperationException($"no Grasshopper object with nickname or id '{nickname}' is on the canvas.");
        if (obj is not IGH_Param param)
        {
            throw new InvalidOperationException($"'{Nick(obj)}' is a {obj.Name}, not an input parameter that can reference document geometry.");
        }

        var clearing = objectIds is null;
        var ids = clearing ? Array.Empty<Guid>() : ToGuids(objectIds!).ToArray();
        param.ClearData();
        // A clear on a parameter that never took referenced geometry is a no-op, not an error; only a real
        // set (with ids) complains when the parameter type cannot hold referenced geometry.
        if (!SetReferences(param, ids) && !clearing)
        {
            throw new InvalidOperationException($"'{Nick(obj)}' is a {obj.Name} parameter, which does not take referenced document geometry (a Curve/Brep/Surface/Mesh/Point parameter does).");
        }

        param.ExpireSolution(recompute: false);
    }

    public void Solve(object grasshopperDocument, bool expireAll)
    {
        ((GH_Document)grasshopperDocument).NewSolution(expireAll);
    }

    // Budget caps for the read half (PRD §10): keep a large data tree within the response budget. The char
    // ceiling is enforced downstream when a script returns the DTO (ReturnValueFormatter); these bound how
    // much we materialise in the first place, and each true total is reported so nothing is hidden silently.
    private const int MaxGetItems = 500;
    private const int MaxBranches = 50;
    private const int MaxItemsPerBranch = 200;

    public GrasshopperValue Get(object grasshopperDocument, string nickname)
    {
        var param = RequireParam((GH_Document)grasshopperDocument, nickname, out var obj);
        var data = param.VolatileData;
        var total = data.DataCount;
        var items = new List<GrasshopperItem>();
        foreach (var goo in data.AllData(false))
        {
            if (items.Count >= MaxGetItems) break;
            items.Add(DescribeGoo(goo as IGH_Goo));
        }

        return new GrasshopperValue(Nick(obj), obj.Name ?? obj.GetType().Name, total, items.ToArray(), items.Count < total);
    }

    public GrasshopperData Data(object grasshopperDocument, string nickname)
    {
        var param = RequireParam((GH_Document)grasshopperDocument, nickname, out var obj);
        var data = param.VolatileData;
        var branchCount = data.PathCount;
        var itemCount = data.DataCount;
        var truncated = false;
        var branches = new List<GrasshopperBranch>();
        foreach (var path in data.Paths)
        {
            if (branches.Count >= MaxBranches) { truncated = true; break; }
            var branch = data.get_Branch(path);
            var items = new List<GrasshopperItem>();
            var count = branch?.Count ?? 0;
            if (branch != null)
            {
                foreach (var goo in branch)
                {
                    if (items.Count >= MaxItemsPerBranch) { truncated = true; break; }
                    items.Add(DescribeGoo(goo as IGH_Goo));
                }
            }

            branches.Add(new GrasshopperBranch(path.ToString(), count, items.ToArray()));
        }

        string? note = null;
        if (truncated)
        {
            note = branches.Count < branchCount
                ? $"showing {branches.Count} of {branchCount} branches (branch cap {MaxBranches}); read narrower via ghdoc for the rest."
                : $"all {branchCount} branches shown, but at least one was capped at {MaxItemsPerBranch} items; read narrower via ghdoc for the rest.";
        }
        return new GrasshopperData(Nick(obj), obj.Name ?? obj.GetType().Name, branchCount, itemCount, branches.ToArray(), truncated, note);
    }

    // ---- helpers ----

    private static IGH_Param RequireParam(GH_Document doc, string nickname, out IGH_DocumentObject obj)
    {
        obj = FindObject(doc, nickname)
            ?? throw new InvalidOperationException($"no Grasshopper object with nickname or id '{nickname}' is on the canvas.");
        return obj as IGH_Param
            ?? throw new InvalidOperationException($"'{Nick(obj)}' is a {obj.Name}, not a parameter with a data tree; address a parameter (or a component's output parameter) by nickname to read its data.");
    }

    /// <summary>Summarises one goo for the read half: numbers/text/booleans verbatim, geometry as its type
    /// plus bounding box and (when it references a document object) that object's id — never the geometry
    /// itself.</summary>
    private static GrasshopperItem DescribeGoo(IGH_Goo? goo)
    {
        switch (goo)
        {
            case null:
                return new GrasshopperItem("other", null, "null", null, null);
            case GH_Number n:
                return new GrasshopperItem("number", n.Value, "Number", null, null);
            case GH_Integer i:
                return new GrasshopperItem("number", i.Value, "Integer", null, null);
            case GH_Boolean b:
                return new GrasshopperItem("boolean", b.Value, "Boolean", null, null);
            case GH_String s:
                return new GrasshopperItem("text", s.Value, "Text", null, null);
            case IGH_GeometricGoo geo:
                var handle = geo.ReferenceID == Guid.Empty ? null : geo.ReferenceID.ToString();
                return new GrasshopperItem("geometry", null, SafeTypeName(goo), BoxOf(geo), handle);
            default:
                return new GrasshopperItem("other", SafeToString(goo), SafeTypeName(goo), null, null);
        }
    }

    // A misbehaving custom goo can throw from ToString()/TypeName; degrade that one item rather than abort
    // the whole Get/Data read.
    private static string SafeTypeName(IGH_Goo goo)
    {
        try { return goo.TypeName ?? goo.GetType().Name; }
        catch { return goo.GetType().Name; }
    }

    private static string? SafeToString(IGH_Goo goo)
    {
        try { return goo.ToString(); }
        catch { return null; }
    }

    private static double[]? BoxOf(IGH_GeometricGoo geo)
    {
        try
        {
            var b = geo.Boundingbox;
            return b.IsValid ? new[] { b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z } : null;
        }
        catch
        {
            return null;
        }
    }

    private static IGH_DocumentObject? FindObject(GH_Document doc, string nicknameOrGuid)
    {
        var byGuid = Guid.TryParse(nicknameOrGuid, out var g);
        foreach (var obj in doc.Objects)
        {
            if (obj is null) continue;
            if (byGuid ? obj.InstanceGuid == g : string.Equals(Nick(obj), nicknameOrGuid, StringComparison.OrdinalIgnoreCase))
            {
                return obj;
            }
        }

        return null;
    }

    private static GrasshopperComponent Describe(IGH_DocumentObject obj) =>
        new(obj.InstanceGuid.ToString(), Nick(obj), obj.Name ?? obj.GetType().Name);

    private static string Nick(IGH_DocumentObject obj) =>
        string.IsNullOrEmpty(obj.NickName) ? (obj.Name ?? "") : obj.NickName;

    private static decimal ToDecimal(object value) => value switch
    {
        null => throw new InvalidOperationException("a slider needs a number value, got null."),
        // string is IConvertible, so parse it explicitly (a bad string would otherwise throw a raw
        // FormatException from IConvertible.ToDecimal instead of this friendly message).
        string s => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d
            : throw new InvalidOperationException($"a slider needs a number value, got '{s}'."),
        IConvertible c => c.ToDecimal(CultureInfo.InvariantCulture),
        _ when decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
        _ => throw new InvalidOperationException($"a slider needs a number value, got '{value}'."),
    };

    private static bool ToBool(object value) => value switch
    {
        bool b => b,
        string s => bool.TryParse(s, out var b) ? b
            : throw new InvalidOperationException($"a toggle needs a true/false value, got '{s}'."),
        IConvertible c => c.ToBoolean(CultureInfo.InvariantCulture),
        _ when bool.TryParse(value?.ToString(), out var b) => b,
        _ => throw new InvalidOperationException($"a toggle needs a true/false value, got '{value}'."),
    };

    private static void SelectValueListItem(GH_ValueList list, object value)
    {
        var target = value?.ToString() ?? "";
        // Select the first item whose name or expression matches (first-match-wins, like FindObject);
        // refuse rather than silently deselect everything when nothing matches, which would leave the
        // list with no selection and feed the definition null on the next solve.
        var matched = false;
        foreach (var item in list.ListItems)
        {
            var hit = !matched &&
                (string.Equals(item.Name, target, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(item.Expression, target, StringComparison.OrdinalIgnoreCase));
            item.Selected = hit;
            matched |= hit;
        }

        if (!matched)
        {
            var names = string.Join(", ", list.ListItems.Select(i => "'" + i.Name + "'"));
            throw new InvalidOperationException($"'{target}' is not an item on value list '{Nick(list)}'; its items are: {names}.");
        }
    }

    private static IEnumerable<Guid> ToGuids(object objectIds)
    {
        if (objectIds is string s)
        {
            yield return ParseGuid(s);
            yield break;
        }

        if (objectIds is Guid g)
        {
            yield return g;
            yield break;
        }

        if (objectIds is IEnumerable seq)
        {
            foreach (var item in seq)
            {
                yield return item is Guid gg ? gg : ParseGuid(item?.ToString() ?? "");
            }

            yield break;
        }

        yield return ParseGuid(objectIds.ToString() ?? "");
    }

    private static Guid ParseGuid(string s) => Guid.TryParse(s, out var g)
        ? g
        : throw new InvalidOperationException($"'{s}' is not a Rhino object id (a GUID).");

    /// <summary>Replaces a persistent geometry parameter's referenced objects with those <paramref name="ids"/>
    /// (empty clears it), matching the goo type to the parameter. A goo carrying only its ReferenceID makes
    /// Grasshopper load the geometry from the document on the next solve, and track it live. Returns false
    /// when the parameter does not take referenced geometry.</summary>
    private static bool SetReferences(IGH_Param param, Guid[] ids)
    {
        switch (param)
        {
            case Grasshopper.Kernel.Parameters.Param_Curve p: p.PersistentData.Clear(); foreach (var id in ids) p.PersistentData.Append(new GH_Curve { ReferenceID = id }); return true;
            case Grasshopper.Kernel.Parameters.Param_Brep p: p.PersistentData.Clear(); foreach (var id in ids) p.PersistentData.Append(new GH_Brep { ReferenceID = id }); return true;
            case Grasshopper.Kernel.Parameters.Param_Surface p: p.PersistentData.Clear(); foreach (var id in ids) p.PersistentData.Append(new GH_Surface { ReferenceID = id }); return true;
            case Grasshopper.Kernel.Parameters.Param_Mesh p: p.PersistentData.Clear(); foreach (var id in ids) p.PersistentData.Append(new GH_Mesh { ReferenceID = id }); return true;
            case Grasshopper.Kernel.Parameters.Param_Point p: p.PersistentData.Clear(); foreach (var id in ids) p.PersistentData.Append(new GH_Point { ReferenceID = id }); return true;
            default: return false;
        }
    }
}
