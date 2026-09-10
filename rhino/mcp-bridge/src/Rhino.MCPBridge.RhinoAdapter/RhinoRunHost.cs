using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.MCPBridge.Core.Connection;
using Rhino.MCPBridge.Core.Execution;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// The real <see cref="IRunHost"/> (rhino/docs/PRD.md §06/§07; spikes §3): runs a script inside the
/// bridge's own command via RhinoApp.ExecuteCommand, reverts with ExecuteCommand(_Undo) afterwards,
/// resolves document ids with the same identity rule `register` uses, and feeds document events to
/// the mutation tracker. Every method runs on the main thread (the launcher guarantees it).
/// </summary>
internal sealed class RhinoRunHost : IRunHost
{
    private readonly Guid _processSalt;
    private readonly bool _caseInsensitivePaths;
    private readonly string _runCommandName;
    private readonly Func<Guid> _runCommandId;
    private readonly Action<string> _log;

    /// <param name="runCommandId">The bridge's run command's Command.Id, read lazily because Rhino
    /// instantiates command classes after the plug-in's OnLoad.</param>
    public RhinoRunHost(Guid processSalt, bool caseInsensitivePaths, string bridgeVersion, string runCommandName, Func<Guid> runCommandId, Action<string> log)
    {
        _processSalt = processSalt;
        _caseInsensitivePaths = caseInsensitivePaths;
        BridgeVersion = bridgeVersion;
        _runCommandName = runCommandName;
        _runCommandId = runCommandId;
        _log = log;
    }

    public string BridgeVersion { get; }

    public RunDocument? ResolveDocument(string documentId)
    {
        if (documentId.Length == 0)
        {
            var active = RhinoDoc.ActiveDoc;
            return active is null ? null : new RunDocument(IdOf(active), active);
        }

        foreach (var doc in RhinoDoc.OpenDocuments())
        {
            if (doc is not null && IdOf(doc) == documentId)
            {
                return new RunDocument(documentId, doc);
            }
        }

        return null;
    }

    public IReadOnlyList<(string DocumentId, string Title, bool Active)> OpenDocuments()
    {
        var active = RhinoDoc.ActiveDoc;
        var list = new List<(string, string, bool)>();
        foreach (var doc in RhinoDoc.OpenDocuments())
        {
            if (doc is null) continue;
            list.Add((IdOf(doc), string.IsNullOrEmpty(doc.Name) ? "Untitled" : doc.Name, active is not null && active.RuntimeSerialNumber == doc.RuntimeSerialNumber));
        }

        return list;
    }

    /// <summary>Same rule as RhinoDocumentSnapshotSource; kept in one place so routing and register agree.</summary>
    private string IdOf(RhinoDoc doc)
    {
        if (string.IsNullOrEmpty(doc.Path))
        {
            return DocumentIdentity.ForUnsaved(_processSalt, string.IsNullOrEmpty(doc.Name) ? "Untitled" : doc.Name);
        }

        return DocumentIdentity.ForSavedPath(RhinoDocumentSnapshotSource.ResolvePathForIdentity(doc.Path, _log), _caseInsensitivePaths);
    }

    public bool RunInCommand(RunDocument run, string undoLabel, Action body)
    {
        var document = (RhinoDoc)run.Raw!;
        if (Command.InCommand())
        {
            return false; // Rhino refuses a nested command; the launcher retries
        }

        // No inner BeginUndoRecord: it does not rename the entry (verified, §17.10) and if a future
        // Rhino honoured it the run would become two entries (review of #282). The label is reported.
        _ = undoLabel;
        RunQueue.Set(body, _log);
        var result = RhinoApp.ExecuteCommand(document, _runCommandName);
        var ran = RunQueue.Ran;
        RunQueue.Clear();
        if (!ran)
        {
            _log($"run command '{_runCommandName}' returned {result} without running the body");
            return false;
        }

        return true;
    }

    public bool UndoLast(RunDocument run)
    {
        var r = RhinoApp.ExecuteCommand((RhinoDoc)run.Raw!, "_Undo");
        return r == Result.Success;
    }

    public bool RedoLast(RunDocument run)
    {
        var r = RhinoApp.ExecuteCommand((RhinoDoc)run.Raw!, "_Redo");
        return r == Result.Success;
    }

    public (Guid Id, string Name) LastCommand()
    {
        var id = Command.LastCommandId;
        return (id, id == Guid.Empty ? "" : Command.LookupCommandName(id, englishName: true) ?? "");
    }

    public bool LastCommandWasOurs()
    {
        // Command.LastCommandId: the id of the command that ran most recently, whatever its style or
        // how it was started. Exact; no ordering assumption (review of #282).
        var ours = _runCommandId();
        return ours != Guid.Empty && Command.LastCommandId == ours;
    }

    public IDisposable SubscribeChanges(RunDocument run, Action<DocumentChange> onChange, Action onAnyChange)
    {
        var serial = ((RhinoDoc)run.Raw!).RuntimeSerialNumber;
        return Subscribe(d => d is not null && d.RuntimeSerialNumber == serial, onChange, _ => onAnyChange());
    }

    public IDisposable MonitorChanges(Action<string> onChange) =>
        Subscribe(d => d is not null, null, d => onChange(IdOf(d)));

    /// <summary>Every document change RhinoCommon reports, filtered by document: object events go to
    /// <paramref name="onChange"/> (netted by the mutation report); every event, object or not --
    /// attributes, layers, materials, groups, block definitions, dimension styles, lights, document
    /// properties -- goes to <paramref name="onAny"/> with its document, because "did the document
    /// change" must not key off the subset the report can count (review of #282).</summary>
    private static IDisposable Subscribe(Func<RhinoDoc?, bool> mine, Action<DocumentChange>? onChange, Action<RhinoDoc> onAny)
    {
        void Added(object? s, RhinoObjectEventArgs e) { var d = e.TheObject?.Document; if (mine(d)) { onAny(d!); onChange?.Invoke(Change(DocumentChange.Kind.Added, e.TheObject!)); } }
        void Deleted(object? s, RhinoObjectEventArgs e) { var d = e.TheObject?.Document; if (mine(d)) { onAny(d!); onChange?.Invoke(Change(DocumentChange.Kind.Deleted, e.TheObject!)); } }
        void Undeleted(object? s, RhinoObjectEventArgs e) { var d = e.TheObject?.Document; if (mine(d)) { onAny(d!); onChange?.Invoke(Change(DocumentChange.Kind.Undeleted, e.TheObject!)); } }
        void Replaced(object? s, RhinoReplaceObjectEventArgs e) { if (mine(e.Document)) { onAny(e.Document); onChange?.Invoke(new DocumentChange(DocumentChange.Kind.Replaced, e.ObjectId, e.NewRhinoObject?.ObjectType.ToString() ?? "", LayerOf(e.NewRhinoObject))); } }
        void Attributes(object? s, RhinoModifyObjectAttributesEventArgs e) { if (mine(e.Document)) onAny(e.Document); }
        void Layers(object? s, Rhino.DocObjects.Tables.LayerTableEventArgs e) { if (mine(e.Document)) onAny(e.Document); }
        void Materials(object? s, Rhino.DocObjects.Tables.MaterialTableEventArgs e) { if (mine(e.Document)) onAny(e.Document); }
        void Groups(object? s, Rhino.DocObjects.Tables.GroupTableEventArgs e) { if (mine(e.Document)) onAny(e.Document); }
        void Blocks(object? s, Rhino.DocObjects.Tables.InstanceDefinitionTableEventArgs e) { if (mine(e.Document)) onAny(e.Document); }
        void DimStyles(object? s, Rhino.DocObjects.Tables.DimStyleTableEventArgs e) { if (mine(e.Document)) onAny(e.Document); }
        void Lights(object? s, Rhino.DocObjects.Tables.LightTableEventArgs e) { if (mine(e.Document)) onAny(e.Document); }
        void Props(object? s, DocumentEventArgs e) { if (mine(e.Document)) onAny(e.Document); }
        RhinoDoc.AddRhinoObject += Added;
        RhinoDoc.DeleteRhinoObject += Deleted;
        RhinoDoc.UndeleteRhinoObject += Undeleted;
        RhinoDoc.ReplaceRhinoObject += Replaced;
        RhinoDoc.ModifyObjectAttributes += Attributes;
        RhinoDoc.LayerTableEvent += Layers;
        RhinoDoc.MaterialTableEvent += Materials;
        RhinoDoc.GroupTableEvent += Groups;
        RhinoDoc.InstanceDefinitionTableEvent += Blocks;
        RhinoDoc.DimensionStyleTableEvent += DimStyles;
        RhinoDoc.LightTableEvent += Lights;
        RhinoDoc.DocumentPropertiesChanged += Props;
        return new Unsubscriber(() =>
        {
            RhinoDoc.AddRhinoObject -= Added;
            RhinoDoc.DeleteRhinoObject -= Deleted;
            RhinoDoc.UndeleteRhinoObject -= Undeleted;
            RhinoDoc.ReplaceRhinoObject -= Replaced;
            RhinoDoc.ModifyObjectAttributes -= Attributes;
            RhinoDoc.LayerTableEvent -= Layers;
            RhinoDoc.MaterialTableEvent -= Materials;
            RhinoDoc.GroupTableEvent -= Groups;
            RhinoDoc.InstanceDefinitionTableEvent -= Blocks;
            RhinoDoc.DimensionStyleTableEvent -= DimStyles;
            RhinoDoc.LightTableEvent -= Lights;
            RhinoDoc.DocumentPropertiesChanged -= Props;
        });
    }

    private static DocumentChange Change(DocumentChange.Kind kind, RhinoObject o) => new(kind, o.Id, o.ObjectType.ToString(), LayerOf(o));

    private static string LayerOf(RhinoObject? o)
    {
        try
        {
            var doc = o?.Document;
            if (o is null || doc is null) return "";
            var layer = doc.Layers[o.Attributes.LayerIndex];
            return layer?.FullPath ?? "";
        }
        catch
        {
            return "";
        }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private Action? _dispose;
        public Unsubscriber(Action dispose) { _dispose = dispose; }
        public void Dispose() { _dispose?.Invoke(); _dispose = null; }
    }
}
