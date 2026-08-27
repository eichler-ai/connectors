using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MCPBridge.Core.Identity;
using MCPBridge.Core.Protocol;
using MCPBridge.Core.Workspace;
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
/// document_id now goes through the real §09 identity scheme (<see cref="DocumentIdentity.Resolve"/>):
/// normalized central-model-path or local-path hashing for a saved document, a fresh `tmp-&lt;guid&gt;`
/// for an unsaved one. Since this handler works with the raw Autodesk.Revit.DB.Document type rather than
/// the IDocumentAdapter seam, each Document is wrapped in a RevitDocumentAdapter before being handed to
/// DocumentIdentity.Resolve. The existing ConditionalWeakTable&lt;Document, string&gt; cache is exactly
/// what makes DocumentIdentity.Resolve's "mints a fresh tmp- id every call for an unsaved document" rule
/// safe to use here: it's still stable across repeated register calls for the SAME still-open Document
/// within this process, the same guarantee the placeholder scheme relied on.
///
/// Best-effort promotion-on-first-save (PRD §09): a cached `tmp-` id is re-resolved on every call (a
/// cached `doc-` id, once minted, is treated as final and never re-resolved); if that re-resolution
/// flips it to a `doc-` id, that's the first-save transition -- see <see cref="ResolveDocumentId"/> for
/// the rename-in-place + short-lived alias handling. Deliberately NOT implemented: promotion for a later
/// Save-As of an already-`doc-` document to a different location (PRD §09 says "the same rename-and-alias
/// path handles a later Save-As... it isn't a special case" in principle, but this handler doesn't
/// currently re-resolve an already-`doc-` id to detect that case -- left as a known gap rather than
/// re-resolving on every call, which would cost a UNC-resolve + hash per register for every saved
/// document just to catch an uncommon path-change case).
/// </summary>
public sealed class DocumentSnapshotHandler : IExternalEventHandler
{
    private readonly object _lock = new();
    private readonly ConditionalWeakTable<Document, string> _documentIds = new();
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

        var documentId = ResolveDocumentId(document);

        return new RegisteredDocument(documentId, document.Title, string.IsNullOrEmpty(path) ? null : path, isWorkshared, isActive);
    }

    /// <summary>
    /// See the class doc comment for the caching/promotion contract this implements. Every path
    /// through this method is best-effort -- a failure resolving or promoting identity must never
    /// break the caller's whole register snapshot, only degrade to reusing the cached id.
    /// </summary>
    private string ResolveDocumentId(Document document)
    {
        if (!_documentIds.TryGetValue(document, out var cachedId))
        {
            var freshId = SafeResolve(document, fallback: null) ?? ("tmp-" + Guid.NewGuid());
            _documentIds.Add(document, freshId);
            return freshId;
        }

        if (!cachedId.StartsWith("tmp-", StringComparison.Ordinal))
        {
            // Already a durable doc- id -- treated as final (see class doc comment's known gap re:
            // a later Save-As to a different location).
            return cachedId;
        }

        var promotedId = SafeResolve(document, fallback: cachedId);
        if (promotedId is null || !promotedId.StartsWith("doc-", StringComparison.Ordinal))
        {
            return cachedId; // still unsaved, or resolution failed -- keep the existing tmp- id.
        }

        // Promotion on first save (PRD §09): rename the old workspace folder in place and register a
        // short-lived alias so an agent still holding the old tmp- id, and anything already written
        // under it, isn't orphaned. Both best-effort -- never let a failure here break this snapshot.
        try
        {
            WorkspacePaths.TryPromoteDocumentRoot(cachedId, promotedId);
            WorkspacePaths.RegisterAlias(cachedId, promotedId);
        }
        catch
        {
        }

        _documentIds.AddOrUpdate(document, promotedId);
        return promotedId;
    }

    private string? SafeResolve(Document document, string? fallback)
    {
        try
        {
            return DocumentIdentity.Resolve(new RevitDocumentAdapter(document), _uncPathResolver);
        }
        catch
        {
            return fallback;
        }
    }

    public string GetName() => "MCP Bridge document snapshot";
}
