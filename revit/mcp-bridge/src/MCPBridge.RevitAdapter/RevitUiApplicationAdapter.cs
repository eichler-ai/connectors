using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Real implementation wrapping Autodesk.Revit.UI.UIApplication. Not unit-tested (see RevitTransactionAdapter).
///
/// Internal for the same reason as <see cref="RevitDocumentAdapter"/>, and it is the more powerful of the
/// two: its constructor takes the UIApplication a script already holds as a global, and its
/// IDocumentCreationSource members below hand back an IDocumentAdapter whose CreateTransaction a script
/// could then call. Public, it was a one-line route to an unmanaged transaction on a brand-new document.
/// </summary>
internal sealed class RevitUiApplicationAdapter : IUiApplicationAdapter, IRawUiApplicationSource, IDocumentCreationSource, IExistingDocumentSource, IDocumentChangeSource
{
    /// <summary>
    /// Category resolution cap per event (#146 Phase 2). Resolving a category is a Document.GetElement
    /// per id, on the UI thread, inside the commit that raised the event; a script that touches a whole
    /// model would otherwise pay for it twice. Past the cap the ids still count (the totals stay exact)
    /// and the event is flagged truncated so by_category is known to undercount.
    /// </summary>
    private const int CategoryResolutionCap = 20_000;

    /// <summary>
    /// See <see cref="IDocumentChangeSource"/>. The handler NEVER throws: it runs inside Revit's own
    /// event dispatch on the UI thread, where an unhandled exception is Revit's to deal with, not ours --
    /// a broken report is not worth a broken session, so any failure drops that one event.
    /// </summary>
    public IDisposable Subscribe(Action<DocumentChange> onChange)
    {
        EventHandler<Autodesk.Revit.DB.Events.DocumentChangedEventArgs> handler = (_, e) =>
        {
            DocumentChange change;
            try
            {
                change = Translate(e);
            }
            catch
            {
                return;
            }

            onChange(change);
        };

        _uiApplication.Application.DocumentChanged += handler;
        return new Unsubscriber(() => _uiApplication.Application.DocumentChanged -= handler);
    }

    private static DocumentChange Translate(Autodesk.Revit.DB.Events.DocumentChangedEventArgs e)
    {
        var document = e.GetDocument();
        var documentId = TryResolveId(document) ?? "(unknown document)";
        var operationName = e.Operation.ToString();
        var operation = operationName switch
        {
            "TransactionCommitted" => DocumentChangeOperation.Committed,
            "TransactionUndone" => DocumentChangeOperation.Undone,
            "TransactionRedone" => DocumentChangeOperation.Redone,
            _ => DocumentChangeOperation.Other,
        };

        var budget = CategoryResolutionCap;
        var truncated = false;
        var added = Resolve(document, e.GetAddedElementIds(), ref budget, ref truncated);
        var modified = Resolve(document, e.GetModifiedElementIds(), ref budget, ref truncated);
        var deleted = e.GetDeletedElementIds().Select(id => id.Value).ToArray();

        return new DocumentChange(documentId, operation, operationName, e.GetTransactionNames().ToArray(), added, modified, deleted, truncated);
    }

    private static IReadOnlyList<ChangedElement> Resolve(Autodesk.Revit.DB.Document document, ICollection<Autodesk.Revit.DB.ElementId> ids, ref int budget, ref bool truncated)
    {
        var result = new List<ChangedElement>(ids.Count);
        foreach (var id in ids)
        {
            string? category = null;
            if (budget > 0)
            {
                budget--;
                try
                {
                    category = document.GetElement(id)?.Category?.Name;
                }
                catch
                {
                    // An element mid-transition can throw from Category; it still counts, uncategorised.
                }
            }
            else
            {
                truncated = true;
            }

            result.Add(new ChangedElement(id.Value, category));
        }

        return result;
    }

    private sealed class Unsubscriber : IDisposable
    {
        private Action? _dispose;
        public Unsubscriber(Action dispose) => _dispose = dispose;
        public void Dispose()
        {
            var dispose = _dispose;
            _dispose = null;
            try
            {
                dispose?.Invoke();
            }
            catch
            {
                // Unsubscribing after the Application is gone is not worth failing a run over.
            }
        }
    }

    private readonly UIApplication _uiApplication;

    public RevitUiApplicationAdapter(UIApplication uiApplication)
    {
        _uiApplication = uiApplication;
        ActiveUiDocument = uiApplication.ActiveUIDocument is { } doc
            ? new RevitUiDocumentAdapter(doc)
            : null;
    }

    public IUiDocumentAdapter? ActiveUiDocument { get; }

    /// <summary>The real UIApplication this adapter wraps (PRD §14) -- see IDocumentAdapter.RawDocument.</summary>
    public UIApplication RawUiApplication => _uiApplication;

    /// <summary>
    /// See <see cref="IDocumentCreationSource"/>. `Application` is an ordinary property on the real
    /// UIApplication (PRD §14, "Application-level access needed no new plumbing") -- nothing new is
    /// exposed here that a script could not already reach; what this adds is that the returned
    /// document gets wrapped in an IDocumentAdapter the executor can open a managed transaction on.
    /// </summary>
    public IDocumentAdapter CreateProjectDocument(string? templatePath)
    {
        var application = _uiApplication.Application;
        var resolvedTemplatePath = string.IsNullOrEmpty(templatePath)
            ? application.DefaultProjectTemplate
            : templatePath;

        if (string.IsNullOrEmpty(resolvedTemplatePath))
        {
            // Fail with the actual condition rather than letting Revit throw on an empty path (PRD
            // §01): DefaultProjectTemplate is per-install and genuinely can be blank.
            throw new InvalidOperationException(
                "No project template was given and this Revit install's Application.DefaultProjectTemplate " +
                "is empty, so there is nothing to create a project document from. Pass an explicit " +
                "template path to CreateProjectDocument.");
        }

        return new RevitDocumentAdapter(application.NewProjectDocument(resolvedTemplatePath));
    }

    /// <summary>See <see cref="IDocumentCreationSource"/>.</summary>
    public IDocumentAdapter CreateFamilyDocument(string templatePath)
    {
        if (string.IsNullOrEmpty(templatePath))
        {
            throw new ArgumentException(
                "A family template path is required -- unlike project documents there is no " +
                "install-wide default family template to fall back on.",
                nameof(templatePath));
        }

        return new RevitDocumentAdapter(_uiApplication.Application.NewFamilyDocument(templatePath));
    }

    /// <summary>
    /// See <see cref="IExistingDocumentSource"/>. Nothing beyond RevitDocumentAdapter's ordinary
    /// constructor -- the document already exists (found by the script, not created here), so there is
    /// no Revit API call to make, unlike CreateProjectDocument/CreateFamilyDocument above.
    /// </summary>
    public IDocumentAdapter WrapExisting(Autodesk.Revit.DB.Document document) => new RevitDocumentAdapter(document);

    /// <summary>
    /// See <see cref="IUiApplicationAdapter.OpenDocuments"/>. Identities via
    /// DocumentIdentity.ResolveCached -- the same shared cache the register snapshot and Publish's
    /// workspace pathing use, so routing, list_instances, and file exchange all agree on one id for
    /// one live document. IsActive compares §09 identities (plus reference equality as the exact-when-
    /// it-holds fast path), mirroring DocumentSnapshotHandler's measured finding that Revit hands back
    /// distinct wrappers for the same document across API entry points.
    /// </summary>
    public IReadOnlyList<OpenDocumentInfo> OpenDocuments
    {
        get
        {
            var activeDocument = _uiApplication.ActiveUIDocument?.Document;
            var activeDocumentId = TryResolveId(activeDocument);
            var result = new List<OpenDocumentInfo>();
            foreach (Autodesk.Revit.DB.Document document in _uiApplication.Application.Documents)
            {
                if (document.IsLinked)
                {
                    continue; // PRD §09: linked documents get no identity and are not addressable.
                }

                var documentId = TryResolveId(document);
                if (documentId is null)
                {
                    continue; // mid-transition document whose identity accessors threw -- best-effort, same as the snapshot.
                }

                var isActive = ReferenceEquals(document, activeDocument)
                    || (activeDocumentId is not null && documentId == activeDocumentId);
                result.Add(new OpenDocumentInfo(documentId, document.Title, isActive));
            }

            return result;
        }
    }

    /// <summary>See <see cref="IUiApplicationAdapter.FindOpenDocument"/>.</summary>
    public IDocumentAdapter? FindOpenDocument(string documentId)
    {
        foreach (Autodesk.Revit.DB.Document document in _uiApplication.Application.Documents)
        {
            if (document.IsLinked)
            {
                continue;
            }

            if (TryResolveId(document) == documentId)
            {
                return new RevitDocumentAdapter(document);
            }
        }

        return null;
    }

    private static string? TryResolveId(Autodesk.Revit.DB.Document? document)
    {
        if (document is null)
        {
            return null;
        }

        try
        {
            return DocumentIdentity.ResolveCached(document, new Win32UncPathResolver());
        }
        catch
        {
            // Same best-effort posture as DocumentSnapshotHandler.BuildEntry: a document
            // mid-transition can throw from the path accessors identity resolves through.
            return null;
        }
    }
}
