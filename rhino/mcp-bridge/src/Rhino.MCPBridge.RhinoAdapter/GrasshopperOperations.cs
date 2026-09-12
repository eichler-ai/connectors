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

        var ids = objectIds is null ? Array.Empty<Guid>() : ToGuids(objectIds).ToArray();
        param.ClearData();
        if (!SetReferences(param, ids))
        {
            throw new InvalidOperationException($"'{Nick(obj)}' is a {obj.Name} parameter, which does not take referenced document geometry (a Curve/Brep/Surface/Mesh/Point parameter does).");
        }

        param.ExpireSolution(recompute: false);
    }

    public void Solve(object grasshopperDocument, bool expireAll)
    {
        ((GH_Document)grasshopperDocument).NewSolution(expireAll);
    }

    // ---- helpers ----

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
        IConvertible c => c.ToDecimal(CultureInfo.InvariantCulture),
        _ when decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
        _ => throw new InvalidOperationException($"a slider needs a number value, got '{value}'."),
    };

    private static bool ToBool(object value) => value switch
    {
        bool b => b,
        IConvertible c => c.ToBoolean(CultureInfo.InvariantCulture),
        _ when bool.TryParse(value?.ToString(), out var b) => b,
        _ => throw new InvalidOperationException($"a toggle needs a true/false value, got '{value}'."),
    };

    private static void SelectValueListItem(GH_ValueList list, object value)
    {
        var target = value?.ToString() ?? "";
        foreach (var item in list.ListItems)
        {
            if (string.Equals(item.Name, target, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Expression, target, StringComparison.OrdinalIgnoreCase))
            {
                item.Selected = true;
            }
            else
            {
                item.Selected = false;
            }
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
