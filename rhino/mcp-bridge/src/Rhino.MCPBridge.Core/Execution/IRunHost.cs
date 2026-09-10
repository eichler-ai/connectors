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

    /// <summary>The name of the most recent command Rhino ran, or null. The executor checks it is its
    /// own before undoing, so a person's action landing between the run and the rollback is never reverted.</summary>
    string? MostRecentCommandName();

    /// <summary>Subscribes to the document's add/delete/replace/undelete events for the run's duration.</summary>
    IDisposable SubscribeChanges(RunDocument document, Action<DocumentChange> onChange);
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
