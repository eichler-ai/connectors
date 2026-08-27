using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MCPBridge.Core.Protocol;
using MCPBridge.RevitAdapter;

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
/// document_id goes through the real §09 identity scheme via DocumentIdentity.ResolveCached
/// (MCPBridge.RevitAdapter) -- the SAME shared, process-lifetime cache RevitDocumentAdapter.DocumentId
/// uses to build the exports/imports workspace for execute_script/Publish, so both agree on the same id
/// for the same live document. See that method's own doc comment for the caching/re-resolve contract:
/// a `tmp-` id is re-resolved on every call (a document may have been saved since the last one) and, if
/// that now returns a `doc-` id, the cache updates to it; a `doc-` id is treated as final.
/// </summary>
public sealed class DocumentSnapshotHandler : IExternalEventHandler
{
    private readonly object _lock = new();
    private readonly IUncPathResolver _uncPathResolver;
    private TaskCompletionSource<List<RegisteredDocument>>? _pending;

    public DocumentSnapshotHandler(IUncPathResolver? uncPathResolver = null)
    {
        _uncPathResolver = uncPathResolver ?? new Win32UncPathResolver();
    }

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

        // Revit does not promise that two API calls asking for "the same" document hand back the same
        // managed wrapper -- Document is a thin wrapper over a native object, and
        // Application.Documents's enumeration and ActiveUIDocument.Document can each mint their own.
        // ReferenceEquals between them was therefore false even for a single open, genuinely active
        // document, so active was reported false for EVERY document, always.
        //
        // Measured, not inferred: with a sole open document that execute_script confirmed WAS the
        // ActiveUiDocument, an instrumented build logged
        //     refEquals=False  activeId=doc-C1ED49972F0D4F4C  id=doc-C1ED49972F0D4F4C
        // on every snapshot -- the two wrappers differ while the §09 identities match exactly.
        //
        // Compare the PRD §09 identity as well, which is derived from the document's path rather than
        // from wrapper identity, and so survives a fresh wrapper. ReferenceEquals is kept as the first
        // test because it's exact when it does hold, and because it's the only one of the two that can
        // identify an UNSAVED document (which has no path to derive a stable id from -- see the
        // residual limitation noted on DocumentIdentity.ResolveCached's tmp- ids below).
        string? activeDocumentId = null;
        if (activeDocument is not null)
        {
            try
            {
                activeDocumentId = DocumentIdentity.ResolveCached(activeDocument, _uncPathResolver);
            }
            catch
            {
                // Same best-effort posture as BuildEntry: a document mid-transition can throw from the
                // path accessors this resolves through. Fall back to reference comparison alone.
            }
        }

        var result = new List<RegisteredDocument>();

        foreach (Document document in app.Application.Documents)
        {
            if (document.IsLinked)
            {
                // PRD §09: "A linked document ... gets no doc-/tmp- ID, no workspace directory, and no
                // list_instances entry."
                continue;
            }

            result.Add(BuildEntry(document, activeDocument, activeDocumentId));
        }

        return result;
    }

    private RegisteredDocument BuildEntry(Document document, Document? activeDocument, string? activeDocumentId)
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

        var documentId = DocumentIdentity.ResolveCached(document, _uncPathResolver);

        // Residual limitation, deliberately not papered over: an UNSAVED document has no path, so its
        // id is a freshly-minted tmp- GUID cached against the wrapper reference (ConditionalWeakTable,
        // which is itself reference-keyed). If Revit hands out distinct wrappers, two tmp- ids for the
        // same unsaved document won't match and only the ReferenceEquals arm can identify it.
        var isActive =
            ReferenceEquals(document, activeDocument)
            || (activeDocumentId is not null
                && !activeDocumentId.StartsWith("tmp-", StringComparison.Ordinal)
                && string.Equals(documentId, activeDocumentId, StringComparison.Ordinal));

        return new RegisteredDocument(documentId, document.Title, string.IsNullOrEmpty(path) ? null : path, isWorkshared, isActive);
    }

    public string GetName() => "MCP Bridge document snapshot";
}
