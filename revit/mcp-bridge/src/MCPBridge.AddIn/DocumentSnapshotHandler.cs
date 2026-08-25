using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MCPBridge.Core.Protocol;

namespace MCPBridge.AddIn;

/// <summary>
/// A dedicated IExternalEventHandler used only to snapshot the current list of open documents (plus which
/// one is active) for the `register` notification's document list (PRD §05: "the list of currently open
/// documents (updated on open/close)"). A real Autodesk.Revit.ApplicationServices.Application (the
/// `UIApplication.Application` object, which exposes `.Documents`) is only reachable from inside an
/// IExternalEventHandler.Execute(UIApplication) callback -- there is no live UIApplication available yet
/// at OnStartup, and register is (re-)sent on every reconnect, not just once, so this can't be captured
/// once and cached.
///
/// Deliberately bypasses the IUiApplicationAdapter/IDocumentAdapter seam MCPBridge.Core's unit-tested
/// logic goes through: those interfaces intentionally expose only Phase 1's trivial-expression-script
/// needs (see IDocumentAdapter's own doc comment) and have no notion of "every open document," only the
/// active one. Building a register snapshot is pure AddIn-side glue that is never unit-tested (like
/// RevitDocumentAdapter and friends), so there is no testability reason to route it through that seam --
/// using the real Revit API types directly here is both simpler and unavoidable.
///
/// KNOWN SIMPLIFICATION: document_id here is a placeholder, not the real §09 identity scheme (normalized
/// central-model-path hashing, doc-/tmp- promotion-on-first-save, alias tracking, etc. -- explicitly
/// Phase 3 scope). Every document -- saved or unsaved -- gets a "doc-"/"tmp-" + GUID minted the first
/// time this handler sees it and cached for the life of the process (a ConditionalWeakTable keyed by the
/// live Document reference), which is close in spirit to "session-scoped GUID minted on open" but not
/// wired to Revit's actual document-open event, only to however often BridgeHost happens to call this
/// snapshot. Deliberately NOT derived from the document's path (an earlier version hashed it): nothing at
/// Phase 1 needs the id to be reproducible across a process restart or a different open of the same
/// file -- only stable across repeated register calls for the SAME still-open Document within THIS
/// process, which a reference-keyed cache already guarantees identically, without the extra hashing code
/// path or its cost. Good enough for Phase 1's register notification; revisit fully when §09 lands.
/// </summary>
public sealed class DocumentSnapshotHandler : IExternalEventHandler
{
    private readonly object _lock = new();
    private readonly ConditionalWeakTable<Document, string> _documentIds = new();
    private TaskCompletionSource<List<RegisteredDocument>>? _pending;

    /// <summary>
    /// Queues a snapshot request and raises <paramref name="externalEvent"/> (which must be an
    /// ExternalEvent created for this handler instance). Never blocks the calling thread -- same shape as
    /// ExternalEventBridge{TResult}.RunAsync, including its Denied/TimedOut handling, deliberately not
    /// reusing that generic class here since it targets IScriptExecutionCallback/IUiApplicationAdapter,
    /// not the raw UIApplication this handler actually needs.
    /// </summary>
    public Task<List<RegisteredDocument>> SnapshotAsync(ExternalEvent externalEvent)
    {
        var tcs = new TaskCompletionSource<List<RegisteredDocument>>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<List<RegisteredDocument>>? orphaned;
        lock (_lock)
        {
            orphaned = _pending;
            _pending = tcs;
        }

        // Second live-wiring review finding: a prior still-queued snapshot request (e.g. a connection torn
        // down/reconnected while its own raise was still sitting in Revit's idle queue) was previously
        // silently overwritten here, leaving its TaskCompletionSource orphaned -- never faulted, never
        // completed, an abandoned Task nothing awaits. Fault it instead, same shape as
        // ExternalEventBridge{TResult}.Abandon().
        orphaned?.TrySetException(new InvalidOperationException("a newer document snapshot request superseded this one before Revit's idle loop reached it."));

        var outcome = externalEvent.Raise();
        if (outcome is ExternalEventRequest.Denied or ExternalEventRequest.TimedOut)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_pending, tcs))
                {
                    _pending = null;
                }
            }

            tcs.TrySetException(new InvalidOperationException($"document snapshot ExternalEvent.Raise() returned {outcome}."));
        }

        return tcs.Task;
    }

    public void Execute(UIApplication app)
    {
        TaskCompletionSource<List<RegisteredDocument>>? pending;
        lock (_lock)
        {
            pending = _pending;
            _pending = null;
        }

        if (pending is null)
        {
            return;
        }

        try
        {
            pending.TrySetResult(BuildSnapshot(app));
        }
        catch (Exception ex)
        {
            pending.TrySetException(ex);
        }
    }

    private List<RegisteredDocument> BuildSnapshot(UIApplication app)
    {
        var activeDocument = app.ActiveUIDocument?.Document;
        var result = new List<RegisteredDocument>();

        foreach (Document document in app.Application.Documents)
        {
            if (document.IsLinked)
            {
                // PRD §09: "A linked document ... gets no doc-/tmp- ID, no workspace directory, and no
                // list_instances entry."
                continue;
            }

            result.Add(BuildEntry(document, ReferenceEquals(document, activeDocument)));
        }

        return result;
    }

    private RegisteredDocument BuildEntry(Document document, bool isActive)
    {
        string? path;
        var isWorkshared = false;
        try
        {
            isWorkshared = document.IsWorkshared;
            if (isWorkshared)
            {
                var centralPath = document.GetWorksharingCentralModelPath();
                path = centralPath is not null ? ModelPathUtils.ConvertModelPathToUserVisiblePath(centralPath) : document.PathName;
            }
            else
            {
                path = document.PathName;
            }
        }
        catch
        {
            // Best-effort: a document mid-transition (e.g. detaching from central) can throw from these
            // accessors -- fall back to treating it as unsaved rather than letting one odd document break
            // the whole register snapshot.
            path = null;
        }

        var prefix = string.IsNullOrEmpty(path) ? "tmp-" : "doc-";
        var documentId = _documentIds.GetValue(document, _ => prefix + Guid.NewGuid());

        return new RegisteredDocument(documentId, document.Title, string.IsNullOrEmpty(path) ? null : path, isWorkshared, isActive);
    }

    public string GetName() => "MCP Bridge document snapshot";
}
