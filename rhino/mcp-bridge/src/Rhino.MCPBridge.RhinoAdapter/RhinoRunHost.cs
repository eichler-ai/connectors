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

    public object? ResolveGrasshopperDocument(string grasshopperDocumentId, out bool notFound)
    {
        notFound = false;
        if (string.IsNullOrEmpty(grasshopperDocumentId))
        {
            return null; // no definition requested; the global is null
        }

        if (!GrasshopperWatcher.GrasshopperLoaded())
        {
            notFound = true; // an id was asked for, but Grasshopper is not even loaded, so nothing matches
            return null;
        }

        var match = FindGrasshopperDocument(grasshopperDocumentId);
        notFound = match is null;
        return match;
    }

    // Stateless; safe to build at plug-in load (its Grasshopper-typed method bodies JIT only when called,
    // from inside a run where a definition is bound and Grasshopper is loaded).
    public IGrasshopperOperations GrasshopperOperations { get; } = new GrasshopperOperations();

    public IGrasshopperSolveScope BeginGrasshopperSolves(object? grasshopperDocument)
    {
        if (!GrasshopperWatcher.GrasshopperLoaded())
        {
            return NullGrasshopperSolveScope.Instance;
        }

        return BeginGrasshopperSolvesCore(grasshopperDocument);
    }

    /// <summary>Grasshopper-typed; only reached once Grasshopper is loaded. Watches the addressed definition,
    /// or the active one when none was addressed (PRD §10).</summary>
    private IGrasshopperSolveScope BeginGrasshopperSolvesCore(object? grasshopperDocument)
    {
        var doc = grasshopperDocument as global::Grasshopper.Kernel.GH_Document ?? ActiveGrasshopperDocument();
        return doc is null ? NullGrasshopperSolveScope.Instance : new GrasshopperSolveScope(doc);
    }

    /// <summary>The active definition via reflection on <c>Grasshopper.Instances.ActiveCanvas.Document</c>
    /// (the canvas is WinForms-typed, which this assembly cannot name — see the snapshot source), or null.</summary>
    private static global::Grasshopper.Kernel.GH_Document? ActiveGrasshopperDocument()
    {
        try
        {
            var instances = Type.GetType("Grasshopper.Instances, Grasshopper");
            var canvas = instances?.GetProperty("ActiveCanvas")?.GetValue(null);
            return canvas?.GetType().GetProperty("Document")?.GetValue(canvas) as global::Grasshopper.Kernel.GH_Document;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Grasshopper-typed; only reached once <see cref="GrasshopperWatcher.GrasshopperLoaded"/> is
    /// true, so the JIT resolves Grasshopper.dll only after the guard.</summary>
    private object? FindGrasshopperDocument(string grasshopperDocumentId)
    {
        var server = global::Grasshopper.Instances.DocumentServer;
        if (server is null)
        {
            return null;
        }

        foreach (global::Grasshopper.Kernel.GH_Document ghdoc in server)
        {
            if (ghdoc is not null && GrasshopperIdentity.IdOf(ghdoc, _processSalt, _caseInsensitivePaths, _log) == grasshopperDocumentId)
            {
                return ghdoc;
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

    public IReadOnlyList<DocumentSaveState> RestartSaveStates()
    {
        var list = new List<DocumentSaveState>();
        foreach (var doc in RhinoDoc.OpenDocuments())
        {
            if (doc is null) continue;
            var title = string.IsNullOrEmpty(doc.Name) ? "Untitled" : doc.Name;
            var path = string.IsNullOrEmpty(doc.Path) ? null : doc.Path;
            list.Add(new DocumentSaveState("rhino", title, path, doc.Modified));
        }

        // GH definitions can be unsaved too (their multi-save prompt is what blocks a quit); include them,
        // but only once Grasshopper is loaded, and in a separate method so the JIT resolves Grasshopper.dll
        // only past that guard.
        if (GrasshopperWatcher.GrasshopperLoaded())
        {
            AddGrasshopperSaveStates(list);
        }

        return list;
    }

    private void AddGrasshopperSaveStates(List<DocumentSaveState> list)
    {
        try
        {
            var server = global::Grasshopper.Instances.DocumentServer;
            if (server is null) return;
            foreach (global::Grasshopper.Kernel.GH_Document ghdoc in server)
            {
                if (ghdoc is null) continue;
                var title = string.IsNullOrEmpty(ghdoc.DisplayName) ? "Untitled" : ghdoc.DisplayName;
                var path = string.IsNullOrEmpty(ghdoc.FilePath) ? null : ghdoc.FilePath;
                list.Add(new DocumentSaveState("grasshopper", title, path, ghdoc.IsModified));
            }
        }
        catch (Exception ex)
        {
            _log($"grasshopper save-state snapshot failed (definitions omitted from restart_snapshot): {ex.Message}");
        }
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

    public IDisposable MonitorChanges(Action<string> onChange)
    {
        // For the life of the plug-in, on every object event: the id is memoised per document serial
        // (IdOf resolves the path on disk and hashes it -- not per object, review of #285) and forgotten
        // when the document's identity can change (open, save-as, close).
        var ids = new Dictionary<uint, string>();
        void Invalidate(object? s, DocumentEventArgs e) { try { ids.Remove(e.DocumentSerialNumber); } catch { } }
        void InvalidateOpen(object? s, DocumentOpenEventArgs e) { try { ids.Remove(e.DocumentSerialNumber); } catch { } }
        void InvalidateSave(object? s, DocumentSaveEventArgs e) { try { ids.Remove(e.DocumentSerialNumber); } catch { } }
        RhinoDoc.EndOpenDocument += InvalidateOpen;
        RhinoDoc.EndSaveDocument += InvalidateSave;
        RhinoDoc.CloseDocument += Invalidate;
        var inner = Subscribe(d => d is not null, null, d =>
        {
            try
            {
                if (!ids.TryGetValue(d.RuntimeSerialNumber, out var id))
                {
                    id = IdOf(d);
                    ids[d.RuntimeSerialNumber] = id;
                }

                onChange(id);
            }
            catch (Exception ex)
            {
                // Nothing may escape into Rhino's event dispatch (a crash class, not a bug report).
                _log("change monitor: " + ex.Message);
            }
        });
        return new Unsubscriber(() =>
        {
            inner.Dispose();
            RhinoDoc.EndOpenDocument -= InvalidateOpen;
            RhinoDoc.EndSaveDocument -= InvalidateSave;
            RhinoDoc.CloseDocument -= Invalidate;
        });
    }

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
        // Current-layer changes leave no undo entry; counting them would only force needless confirms.
        void Layers(object? s, Rhino.DocObjects.Tables.LayerTableEventArgs e) { if (e.EventType != Rhino.DocObjects.Tables.LayerTableEventType.Current && mine(e.Document)) onAny(e.Document); }
        void Linetypes(object? s, Rhino.DocObjects.Tables.LinetypeTableEventArgs e) { if (mine(e.Document)) onAny(e.Document); }
        void Hatches(object? s, Rhino.DocObjects.Tables.HatchPatternTableEventArgs e) { if (mine(e.Document)) onAny(e.Document); }
        void RenderContent(object? s, RhinoDoc.RenderContentTableEventArgs e) { if (mine(e.Document)) onAny(e.Document); }
        void UserStrings(object? s, RhinoDoc.UserStringChangedArgs e) { if (mine(e.Document)) onAny(e.Document); }
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
        RhinoDoc.LinetypeTableEvent += Linetypes;
        RhinoDoc.HatchPatternTableEvent += Hatches;
        RhinoDoc.RenderMaterialsTableEvent += RenderContent;
        RhinoDoc.RenderEnvironmentTableEvent += RenderContent;
        RhinoDoc.RenderTextureTableEvent += RenderContent;
        RhinoDoc.UserStringChanged += UserStrings;
        return new Unsubscriber(() =>
        {
            RhinoDoc.LinetypeTableEvent -= Linetypes;
            RhinoDoc.HatchPatternTableEvent -= Hatches;
            RhinoDoc.RenderMaterialsTableEvent -= RenderContent;
            RhinoDoc.RenderEnvironmentTableEvent -= RenderContent;
            RhinoDoc.RenderTextureTableEvent -= RenderContent;
            RhinoDoc.UserStringChanged -= UserStrings;
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
