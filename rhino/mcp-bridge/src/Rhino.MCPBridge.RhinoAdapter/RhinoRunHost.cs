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
    private readonly Action<string> _log;

    public RhinoRunHost(Guid processSalt, bool caseInsensitivePaths, string bridgeVersion, string runCommandName, Action<string> log)
    {
        _processSalt = processSalt;
        _caseInsensitivePaths = caseInsensitivePaths;
        BridgeVersion = bridgeVersion;
        _runCommandName = runCommandName;
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

        RunQueue.Set(() =>
        {
            // Rhino names the entry after the command; an inner record may rename it (open item §17.10 --
            // the harness reads the history to find out). Harmless if it does not.
            var serial = document.BeginUndoRecord(undoLabel);
            try { body(); }
            finally { if (serial != 0) document.EndUndoRecord(serial); }
        });
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

    public string? MostRecentCommandName()
    {
        // Rhino exposes the MRU list as display strings / macros (e.g. "_Undo", "MCPBridgeRun"); the
        // executor compares case-insensitively against its own command name.
        var recent = Command.GetMostRecentCommands();
        if (recent is null || recent.Length == 0) return null;
        for (var i = recent.Length - 1; i >= 0; i--)
        {
            var macro = recent[i].Macro ?? recent[i].DisplayString;
            if (string.IsNullOrWhiteSpace(macro)) continue;
            return macro.Trim().TrimStart('_', '-', '!');
        }

        return null;
    }

    public IDisposable SubscribeChanges(RunDocument run, Action<DocumentChange> onChange)
    {
        var serial = ((RhinoDoc)run.Raw!).RuntimeSerialNumber;
        void Added(object? s, RhinoObjectEventArgs e) { if (e.TheObject?.Document?.RuntimeSerialNumber == serial) onChange(Change(DocumentChange.Kind.Added, e.TheObject)); }
        void Deleted(object? s, RhinoObjectEventArgs e) { if (e.TheObject?.Document?.RuntimeSerialNumber == serial) onChange(Change(DocumentChange.Kind.Deleted, e.TheObject)); }
        void Undeleted(object? s, RhinoObjectEventArgs e) { if (e.TheObject?.Document?.RuntimeSerialNumber == serial) onChange(Change(DocumentChange.Kind.Undeleted, e.TheObject)); }
        void Replaced(object? s, RhinoReplaceObjectEventArgs e) { if (e.Document?.RuntimeSerialNumber == serial) onChange(new DocumentChange(DocumentChange.Kind.Replaced, e.ObjectId, e.NewRhinoObject?.ObjectType.ToString() ?? "", LayerOf(e.NewRhinoObject))); }
        RhinoDoc.AddRhinoObject += Added;
        RhinoDoc.DeleteRhinoObject += Deleted;
        RhinoDoc.UndeleteRhinoObject += Undeleted;
        RhinoDoc.ReplaceRhinoObject += Replaced;
        return new Unsubscriber(() =>
        {
            RhinoDoc.AddRhinoObject -= Added;
            RhinoDoc.DeleteRhinoObject -= Deleted;
            RhinoDoc.UndeleteRhinoObject -= Undeleted;
            RhinoDoc.ReplaceRhinoObject -= Replaced;
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
