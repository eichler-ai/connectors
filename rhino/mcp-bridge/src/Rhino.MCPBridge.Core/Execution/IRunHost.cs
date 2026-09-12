namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// What the executor needs from Rhino, behind the adapter seam so the run lifecycle is tier-1
/// testable (rhino/docs/PRD.md §06/§07; spikes §3). All members are called ON the main thread.
/// </summary>
internal interface IRunHost
{
    /// <summary>The bridge's version string, for the script globals.</summary>
    string BridgeVersion { get; }

    /// <summary>Resolves a document_id to a document; null when no open document has that id.
    /// An empty id means the active document (null when there is none).</summary>
    RunDocument? ResolveDocument(string documentId);

    /// <summary>Resolves a gh_document_id to the open Grasshopper definition (a <c>GH_Document</c>, returned
    /// opaque as <see cref="object"/> — Core must not name Grasshopper types), for the script's
    /// ghdoc/GrasshopperDocument global (PRD §10). Null when the id is empty (none requested) or Grasshopper
    /// is not loaded. <paramref name="notFound"/> is true ONLY when a non-empty id matched no open
    /// definition, so the executor fails loudly instead of silently binding null.</summary>
    object? ResolveGrasshopperDocument(string grasshopperDocumentId, out bool notFound);

    /// <summary>Begins collecting Grasshopper solution events for the run's duration (PRD §10): the addressed
    /// definition (<paramref name="grasshopperDocument"/>, the resolved GH_Document as opaque object) if
    /// given, else the active one. Returns a scope the executor disposes when the run ends; a no-op scope
    /// (its report null) when Grasshopper is not loaded.</summary>
    IGrasshopperSolveScope BeginGrasshopperSolves(object? grasshopperDocument);

    /// <summary>The Grasshopper driving operations behind <c>Connector.Grasshopper</c> (PRD §10), bound into
    /// the script's globals. The implementation names Grasshopper types; Core only forwards through it.</summary>
    IGrasshopperOperations GrasshopperOperations { get; }

    /// <summary>The ids and titles of every open document, for a document-not-found error's candidates.</summary>
    IReadOnlyList<(string DocumentId, string Title, bool Active)> OpenDocuments();

    /// <summary>Runs <paramref name="body"/> inside one Rhino command on <paramref name="document"/>
    /// (RhinoApp.ExecuteCommand of the bridge's own command), naming the command's undo entry
    /// <paramref name="undoLabel"/> where Rhino allows. Returns false when the command could not be
    /// started at all (another command is running) -- the body did not run.</summary>
    bool RunInCommand(RunDocument document, string undoLabel, Action body);

    /// <summary>Issues Rhino's _Undo on <paramref name="document"/> AFTER the run's command has returned
    /// (spikes §3: a mid-command undo destroys the entry). Returns false when nothing was undone.</summary>
    bool UndoLast(RunDocument document);

    /// <summary>Issues Rhino's _Redo on <paramref name="document"/>. Returns false when nothing was redone.</summary>
    bool RedoLast(RunDocument document);

    /// <summary>The command Rhino ran most recently: its id (Command.LastCommandId) and English name.
    /// The undo tool compares the id with the bridge's run command and with its own last undo/redo to
    /// decide whether the top of the stack is the connector's work (PRD §07).</summary>
    (Guid Id, string Name) LastCommand();

    /// <summary>Whether the command Rhino ran most recently is the bridge's own run command
    /// (Command.LastCommandId, exact and order-free -- review of #282). The executor checks it before
    /// undoing, so a person's action landing between the run and the rollback is never reverted.</summary>
    bool LastCommandWasOurs();

    /// <summary>Subscribes to EVERY document's change events for the life of the plug-in and reports
    /// each with its document id, for the undo tool's gate (<see cref="ChangeClock"/>).</summary>
    IDisposable MonitorChanges(Action<string> onChange);

    /// <summary>Subscribes to the document's events for the run's duration. <paramref name="onChange"/>
    /// receives the object events the mutation report nets; <paramref name="onAnyChange"/> fires for
    /// EVERY document change RhinoCommon reports -- object attributes, layers, materials, groups, block
    /// definitions, dimension styles, lights, document properties -- because the rollback decision must
    /// key off "did the run change the document", not off the subset the report can count (review of #282).</summary>
    IDisposable SubscribeChanges(RunDocument document, Action<DocumentChange> onChange, Action onAnyChange);
}

/// <summary>
/// A resolved document as the executor handles it: the id, and the raw RhinoDoc as an opaque object.
/// Opaque on purpose -- Core's executor and its tier-1 tests must never touch RhinoDoc's static
/// state (its type initializer calls Rhino's native core and crashes any process that is not Rhino;
/// a test host that dies mid-run still reports "Passed!" for whatever ran first). Only ScriptGlobals
/// casts Raw back, at the moment a script is about to run.
/// </summary>
internal sealed class RunDocument
{
    public string DocumentId { get; }
    public object? Raw { get; }
    public RunDocument(string documentId, object? raw) { DocumentId = documentId; Raw = raw; }
}
