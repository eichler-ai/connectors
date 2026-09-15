using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Eichler.Connectors.Rhino;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;
using Rhino.MCPBridge.Core.Execution;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// The real <see cref="IGrasshopperOperations"/> (PRD §10): drives a bound <c>GH_Document</c> from a
/// script's <c>Connector.Grasshopper</c>. Grasshopper-typed, so it is only reached from inside a run where
/// a definition was resolved (Grasshopper is loaded). Every method runs on the main thread.
/// </summary>
internal sealed class GrasshopperOperations : IGrasshopperOperations
{
    // The run-scoped Grasshopper undo record: every mutation in a run appends its before-state to this one
    // record (per-run grouping), pushed as a single entry when the run ends. Runs serialise on the UI thread,
    // so a single pending field is safe. Null between runs (and for a run with no bound definition).
    private (GH_Document Doc, GH_UndoRecord Rec)? _pendingUndo;

    public GrasshopperComponent? Find(object grasshopperDocument, string nicknameOrGuid)
    {
        var obj = FindObject((GH_Document)grasshopperDocument, nicknameOrGuid);
        return obj is null ? null : Describe(obj);
    }

    public void Set(object grasshopperDocument, string nickname, object value)
    {
        var doc = (GH_Document)grasshopperDocument;
        var obj = FindObject(doc, nickname)
            ?? throw new InvalidOperationException($"no Grasshopper object with nickname or id '{nickname}' is on the canvas.");

        RecordObjectUndo(doc, obj);
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
        var doc = (GH_Document)grasshopperDocument;
        var obj = FindObject(doc, nickname)
            ?? throw new InvalidOperationException($"no Grasshopper object with nickname or id '{nickname}' is on the canvas.");
        if (obj is not IGH_Param param)
        {
            throw new InvalidOperationException($"'{Nick(obj)}' is a {obj.Name}, not an input parameter that can reference document geometry.");
        }

        RecordObjectUndo(doc, obj);
        var clearing = objectIds is null;
        var ids = clearing ? Array.Empty<Guid>() : ToGuids(objectIds!).ToArray();
        param.ClearData();
        // A clear on a parameter that never took referenced geometry is a no-op, not an error; only a real
        // set (with ids) complains when the parameter type cannot hold referenced geometry.
        if (!SetReferences(param, ids) && !clearing)
        {
            throw new InvalidOperationException($"'{Nick(obj)}' is a {obj.Name} parameter, which does not take referenced document geometry (a Curve/Brep/Surface/Mesh/Point or generic Geometry parameter does).");
        }

        param.ExpireSolution(recompute: false);
    }

    public void Connect(object grasshopperDocument, string sourceId, string sourceOutput, string targetId, string targetInput)
    {
        var doc = (GH_Document)grasshopperDocument;
        var source = ResolvePort(FindObjectOrThrow(doc, sourceId), sourceOutput, output: true);
        var target = ResolvePort(FindObjectOrThrow(doc, targetId), targetInput, output: false);
        // An output-only control (slider/toggle/value list) accepts AddSource at the kernel level but ignores
        // it at solve — a silent no-op. Refuse it loudly (the connector's "never silently" rule), naming the
        // fix, rather than let a script think it wired something.
        if (IsSourceRejectingControl(target))
        {
            throw new InvalidOperationException($"'{Nick((IGH_DocumentObject)target)}' is an output-only control (a Number Slider, Boolean Toggle or Value List): it produces a value and cannot take a wired source. Use it as the wire's source instead.");
        }

        // Idempotent: wiring the same pair twice is a no-op, not a duplicate source (and records no undo).
        if (!target.Sources.Contains(source))
        {
            RecordWireUndo(doc, target);
            target.AddSource(source);
            target.ExpireSolution(recompute: false);
        }
    }

    public void Disconnect(object grasshopperDocument, string sourceId, string sourceOutput, string targetId, string targetInput)
    {
        var doc = (GH_Document)grasshopperDocument;
        var source = ResolvePort(FindObjectOrThrow(doc, sourceId), sourceOutput, output: true);
        var target = ResolvePort(FindObjectOrThrow(doc, targetId), targetInput, output: false);
        // Removing a source that is not wired is a no-op, not an error (matches ClearReference's forgiving clear).
        if (target.Sources.Contains(source))
        {
            RecordWireUndo(doc, target);
            target.RemoveSource(source);
            target.ExpireSolution(recompute: false);
        }
    }

    public void ClearSources(object grasshopperDocument, string targetId, string targetInput)
    {
        var doc = (GH_Document)grasshopperDocument;
        var target = ResolvePort(FindObjectOrThrow(doc, targetId), targetInput, output: false);
        if (target.SourceCount > 0)
        {
            RecordWireUndo(doc, target);
            target.RemoveAllSources();
            target.ExpireSolution(recompute: false);
        }
    }

    public void Save(object grasshopperDocument, string path)
    {
        var doc = (GH_Document)grasshopperDocument;
        var raw = string.IsNullOrWhiteSpace(path) ? doc.FilePath : path;
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("this definition has never been saved, so it has no current path; pass a file path to save it to (a .gh or .ghx).");
        }

        // Resolve to an absolute path so a relative argument does not leave a relative FilePath (which a later
        // Save("") would then resolve against whatever working directory Rhino happens to have).
        var target = System.IO.Path.GetFullPath(raw);
        // GH_DocumentIO.SaveQuiet writes the file (extension picks .gh binary vs .ghx XML) but does NOT set
        // the document's FilePath (verified live), so set it ourselves — otherwise a later Save() to the
        // "current" path would fail and the canvas title would not reflect where it was saved.
        var io = new GH_DocumentIO(doc);
        if (!io.SaveQuiet(target))
        {
            throw new InvalidOperationException($"Grasshopper could not save the definition to '{target}'.");
        }

        doc.FilePath = target;
    }

    public IDisposable BeginUndoRecording(object? grasshopperDocument, string label)
    {
        if (grasshopperDocument is not GH_Document doc)
        {
            return NoOpUndoScope.Instance;
        }

        _pendingUndo = (doc, new GH_UndoRecord(string.IsNullOrWhiteSpace(label) ? "MCP run" : label));
        return new UndoCommit(this, doc);
    }

    // Appends the object's full before-state to the run's undo record (for Set/Reference). No-op when no
    // record is open or it belongs to another document.
    private void RecordObjectUndo(GH_Document doc, IGH_DocumentObject obj)
    {
        if (_pendingUndo is { } p && ReferenceEquals(p.Doc, doc))
        {
            p.Rec.AddAction(new GH_GenericObjectAction(obj));
        }
    }

    // Appends the target parameter's wire (source) before-state to the run's undo record (for Connect/
    // Disconnect/ClearSources).
    private void RecordWireUndo(GH_Document doc, IGH_Param target)
    {
        if (_pendingUndo is { } p && ReferenceEquals(p.Doc, doc))
        {
            p.Rec.AddAction(new GH_WireAction(target));
        }
    }

    // Commits the run's accumulated undo record as ONE entry the user can revert in the Grasshopper editor,
    // or drops it when nothing was recorded. Returned by BeginUndoRecording; disposed at run end.
    private sealed class UndoCommit : IDisposable
    {
        private readonly GrasshopperOperations _ops;
        private readonly GH_Document _doc;
        public UndoCommit(GrasshopperOperations ops, GH_Document doc) { _ops = ops; _doc = doc; }

        public void Dispose()
        {
            if (_ops._pendingUndo is { } p && ReferenceEquals(p.Doc, _doc))
            {
                // Clear the field FIRST: if PushUndoRecord throws, the stale record must not leak into the
                // next run (the executor swallows a dispose failure). Capturing then nulling makes that safe.
                _ops._pendingUndo = null;
                if (p.Rec.ActionCount > 0)
                {
                    _doc.UndoServer.PushUndoRecord(p.Rec);
                }
            }
        }
    }

    private sealed class NoOpUndoScope : IDisposable
    {
        public static readonly NoOpUndoScope Instance = new();
        public void Dispose() { }
    }

    public void Solve(object grasshopperDocument, bool expireAll)
    {
        ((GH_Document)grasshopperDocument).NewSolution(expireAll);
    }

    public GrasshopperComponent Add(object grasshopperDocument, string name, double x, double y)
    {
        var doc = (GH_Document)grasshopperDocument;
        var proxy = ResolveProxy(name);
        var obj = Grasshopper.Instances.ComponentServer.EmitObject(proxy.Guid)
            ?? throw new InvalidOperationException($"Grasshopper could not create '{name}' (its component failed to emit).");
        doc.AddObject(obj, false);
        PlaceAndRecordAdd(doc, obj, x, y);
        obj.ExpireSolution(recompute: false);
        return Describe(obj);
    }

    public GrasshopperComponent AddSlider(object grasshopperDocument, double min, double max, double value, int decimals, double x, double y)
    {
        var doc = (GH_Document)grasshopperDocument;
        var slider = new GH_NumberSlider();
        // Add first: a freshly constructed slider has no Params and null canvas attributes until it is in a
        // document (the tester hit exactly this), so range/value/placement must all follow the AddObject.
        doc.AddObject(slider, false);
        ApplySliderRange(slider, min, max, decimals);
        slider.SetSliderValue(ClampToRange((decimal)value, slider));
        PlaceAndRecordAdd(doc, slider, x, y);
        slider.ExpireSolution(recompute: false);
        return Describe(slider);
    }

    public void SetSliderRange(object grasshopperDocument, string nickname, double min, double max, int decimals)
    {
        var doc = (GH_Document)grasshopperDocument;
        var obj = FindObject(doc, nickname)
            ?? throw new InvalidOperationException($"no Grasshopper object with nickname or id '{nickname}' is on the canvas.");
        if (obj is not GH_NumberSlider slider)
        {
            throw new InvalidOperationException($"'{Nick(obj)}' is a {obj.Name}, not a Number Slider; SetSliderRange sets a slider's range and precision.");
        }

        RecordObjectUndo(doc, slider);
        var current = slider.CurrentValue;
        ApplySliderRange(slider, min, max, decimals);
        slider.SetSliderValue(ClampToRange(current, slider)); // keep the value inside the new range
        slider.ExpireSolution(recompute: false);
    }

    // Budget caps for the read half (PRD §10): keep a large data tree within the response budget. The char
    // ceiling is enforced downstream when a script returns the DTO (ReturnValueFormatter); these bound how
    // much we materialise in the first place, and each true total is reported so nothing is hidden silently.
    private const int MaxGetItems = 500;
    private const int MaxBranches = 50;
    private const int MaxItemsPerBranch = 200;

    public GrasshopperValue Get(object grasshopperDocument, string nickname, string output)
    {
        var param = RequireReadParam((GH_Document)grasshopperDocument, nickname, output, out var obj);
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

    public GrasshopperData Data(object grasshopperDocument, string nickname, string output)
    {
        var param = RequireReadParam((GH_Document)grasshopperDocument, nickname, output, out var obj);
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

    // Resolves the parameter to read from: a free-floating parameter is its own data source, and a component
    // resolves to one of its OUTPUT parameters -- its sole output when `output` is empty, otherwise the one
    // named by `output` (name or 0-based index). ResolvePort(output:true) already carries exactly these rules
    // and the errors that name the choices, so a component can be read directly instead of failing with
    // "not a parameter" (issue #349's sibling, #351: "Robot Output is a component, not a parameter").
    private static IGH_Param RequireReadParam(GH_Document doc, string nickname, string output, out IGH_DocumentObject obj)
    {
        obj = FindObject(doc, nickname)
            ?? throw new InvalidOperationException($"no Grasshopper object with nickname or id '{nickname}' is on the canvas.");
        return ResolvePort(obj, output, output: true);
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

    private static IGH_DocumentObject FindObjectOrThrow(GH_Document doc, string nicknameOrGuid) =>
        FindObject(doc, nicknameOrGuid)
        ?? throw new InvalidOperationException($"no Grasshopper object with nickname or id '{nicknameOrGuid}' is on the canvas.");

    /// <summary>Resolves one of an object's ports to an <see cref="IGH_Param"/> for wiring. A free-floating
    /// parameter (a slider, panel, or a bare Param_*) IS its own single port, so <paramref name="port"/> is
    /// left empty for it. For a component, an empty port means its sole port (an error names the choices when
    /// there is more than one), otherwise the port is matched by 0-based index or by name/nickname
    /// (case-insensitive, first match wins). <paramref name="output"/> selects the output side (a wire's
    /// source) or the input side (its target).</summary>
    private static IGH_Param ResolvePort(IGH_DocumentObject obj, string port, bool output)
    {
        var side = output ? "output" : "input";
        // A free-floating parameter is its own port; a named/indexed port only makes sense for a component.
        if (obj is IGH_Param param && obj is not IGH_Component)
        {
            if (!string.IsNullOrEmpty(port) && port != "0" &&
                !string.Equals(param.Name, port, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(param.NickName, port, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"'{Nick(obj)}' is a parameter, not a component, so it has no {side} ports to address by name or index; pass \"\" as the {side} port.");
            }

            return param;
        }

        if (obj is IGH_Component comp)
        {
            var ports = output ? comp.Params.Output : comp.Params.Input;
            if (ports.Count == 0)
            {
                throw new InvalidOperationException($"'{Nick(obj)}' ({obj.Name}) has no {side} ports.");
            }

            if (string.IsNullOrEmpty(port))
            {
                if (ports.Count == 1)
                {
                    return ports[0];
                }

                throw new InvalidOperationException($"'{Nick(obj)}' ({obj.Name}) has {ports.Count} {side} ports; name which one (by name or 0-based index): {PortNames(ports)}.");
            }

            if (int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
            {
                if (idx < 0 || idx >= ports.Count)
                {
                    throw new InvalidOperationException($"'{Nick(obj)}' ({obj.Name}) has {ports.Count} {side} ports (index 0..{ports.Count - 1}); {idx} is out of range.");
                }

                return ports[idx];
            }

            foreach (var p in ports)
            {
                if (string.Equals(p.Name, port, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.NickName, port, StringComparison.OrdinalIgnoreCase))
                {
                    return p;
                }
            }

            throw new InvalidOperationException($"'{Nick(obj)}' ({obj.Name}) has no {side} port '{port}'; its {side} ports are: {PortNames(ports)}.");
        }

        throw new InvalidOperationException($"'{Nick(obj)}' ({obj.Name}) is neither a component nor a parameter, so it has no ports to wire.");
    }

    // Slider/toggle/value list are input controls a user edits, but wiring-wise they are output-only: their
    // value comes from their own state, and a source added to them is ignored at solve. (A Panel, by contrast,
    // does display a wired source, so it is not in this set.)
    private static bool IsSourceRejectingControl(IGH_Param param) =>
        param is GH_NumberSlider or GH_BooleanToggle or GH_ValueList;

    private static string PortNames(IEnumerable<IGH_Param> ports) =>
        string.Join(", ", ports.Select(p => "'" + (string.IsNullOrEmpty(p.NickName) ? p.Name : p.NickName) + "'"));

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

    // Places a freshly-added object on the canvas (creating its attributes if the emit did not) and records
    // its creation in the run's grouped GH undo entry, so one Ctrl+Z removes everything the run added. When x
    // and y are both 0 the object is auto-offset by the current canvas population, so successive adds do not
    // stack exactly on top of each other.
    private void PlaceAndRecordAdd(GH_Document doc, IGH_DocumentObject obj, double x, double y)
    {
        if (obj.Attributes == null) obj.CreateAttributes();
        if (x == 0 && y == 0)
        {
            var n = doc.ObjectCount;
            x = 120 + (n % 8) * 40;
            y = 120 + (n / 8) * 40;
        }

        obj.Attributes!.Pivot = new System.Drawing.PointF((float)x, (float)y);
        if (_pendingUndo is { } p && ReferenceEquals(p.Doc, doc))
        {
            p.Rec.AddAction(new GH_AddObjectAction(obj));
        }
    }

    private static void ApplySliderRange(GH_NumberSlider slider, double min, double max, int decimals)
    {
        if (max < min) (min, max) = (max, min); // tolerate a swapped range rather than produce an empty one
        slider.Slider.Minimum = (decimal)min;
        slider.Slider.Maximum = (decimal)max;
        slider.Slider.DecimalPlaces = decimals < 0 ? 0 : decimals;
    }

    private static decimal ClampToRange(decimal value, GH_NumberSlider slider) =>
        value < slider.Slider.Minimum ? slider.Slider.Minimum
        : value > slider.Slider.Maximum ? slider.Slider.Maximum
        : value;

    // Resolves an Add target to a catalog proxy: a component/parameter name (case-insensitive), a GUID, or a
    // "Category/Name" when a bare name is shared by more than one component. Obsolete proxies are skipped so a
    // deprecated duplicate never wins.
    private static IGH_ObjectProxy ResolveProxy(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Add needs a component name (e.g. \"Circle\") or a component GUID.");
        }

        var server = Grasshopper.Instances.ComponentServer;
        if (Guid.TryParse(name, out var g))
        {
            foreach (var p in server.ObjectProxies)
            {
                if (p != null && p.Guid == g) return p;
            }

            throw new InvalidOperationException($"no Grasshopper component has the GUID '{name}'.");
        }

        string? category = null;
        var wanted = name;
        var slash = name.IndexOf('/');
        if (slash > 0)
        {
            category = name.Substring(0, slash).Trim();
            wanted = name.Substring(slash + 1).Trim();
        }

        var matches = new List<IGH_ObjectProxy>();
        foreach (var p in server.ObjectProxies)
        {
            if (p?.Desc == null || p.Obsolete) continue;
            if (!string.Equals(p.Desc.Name, wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (category != null && !string.Equals(p.Desc.Category, category, StringComparison.OrdinalIgnoreCase)) continue;
            matches.Add(p);
        }

        if (matches.Count == 1) return matches[0];
        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"no Grasshopper component named '{name}' was found; search for its exact name with search_functions (namespace \"Grasshopper\").");
        }

        var choices = string.Join(", ", matches.Select(m => $"\"{m.Desc.Category}/{m.Desc.Name}\"").Distinct());
        throw new InvalidOperationException($"'{name}' is ambiguous ({matches.Count} components share it); qualify it as Category/Name — one of: {choices}.");
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
    /// (empty clears it): for a typed parameter the goo matches the parameter, for a generic Geometry
    /// parameter it matches each referenced object's own type. A goo carrying only its ReferenceID makes
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
            // The generic Geometry input takes any geometry, so the goo type is not fixed by the parameter --
            // resolve each object in the document and match the goo to what it actually is.
            case Grasshopper.Kernel.Parameters.Param_Geometry p: p.PersistentData.Clear(); foreach (var id in ids) p.PersistentData.Append(ReferencedGooFor(id)); return true;
            default: return false;
        }
    }

    /// <summary>Builds the referenced goo for a generic Geometry input: looks the object up and matches the
    /// goo to its geometry type (so the reference loads and tracks it live). Resolves against the active
    /// document on purpose — Grasshopper itself resolves a goo's ReferenceID against the active RhinoDoc at
    /// solve time, so that is where the geometry will actually load from. Throws a clear error when the object
    /// is missing or its type is not one a Geometry parameter references.</summary>
    private static IGH_GeometricGoo ReferencedGooFor(Guid id)
    {
        var obj = Rhino.RhinoDoc.ActiveDoc?.Objects.FindId(id)
            ?? throw new InvalidOperationException($"no Rhino object with id {id} is in the document to reference into a Geometry parameter.");
        return obj.Geometry switch
        {
            Rhino.Geometry.Curve => new GH_Curve { ReferenceID = id },
            Rhino.Geometry.Brep => new GH_Brep { ReferenceID = id },
            // Extrusion derives from Surface but is a capped solid; Grasshopper references it as a Brep, so
            // match that here (this arm must precede the Surface arm, which would otherwise catch it).
            Rhino.Geometry.Extrusion => new GH_Brep { ReferenceID = id },
            Rhino.Geometry.Surface => new GH_Surface { ReferenceID = id },
            Rhino.Geometry.Mesh => new GH_Mesh { ReferenceID = id },
            Rhino.Geometry.Point => new GH_Point { ReferenceID = id },
            null => throw new InvalidOperationException($"the Rhino object {id} has no geometry to reference into a Geometry parameter."),
            var g => throw new InvalidOperationException($"the Rhino object {id} is a {g.GetType().Name}, which a Geometry parameter does not reference (Curve, Brep, Surface, Mesh or Point objects)."),
        };
    }
}
