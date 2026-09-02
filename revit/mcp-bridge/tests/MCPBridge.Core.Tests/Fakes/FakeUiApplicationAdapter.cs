using System;
using System.Collections.Generic;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Tests.Fakes;

/// <summary>
/// See FakeDocumentAdapter: deliberately does not implement IRawUiApplicationSource (PRD §14).
///
/// It DOES implement IDocumentCreationSource (issue #24), and that difference is the point of that
/// interface's shape: IRawUiApplicationSource returns a real Autodesk.Revit.UI.UIApplication, which a
/// fake genuinely cannot supply and which would drag a direct RevitAPI reference into this test
/// assembly; IDocumentCreationSource returns an IDocumentAdapter, so the create-and-track logic stays
/// tier-1 testable while only the final unwrap to a real Document remains tier-2.
/// </summary>
internal sealed class FakeUiApplicationAdapter : IUiApplicationAdapter, IDocumentCreationSource, IDocumentChangeSource, IPostableCommandSource
{
    /// <summary>Every PostUndo/PostRedo, in order (#146 Phase 2c).</summary>
    public List<string> PostedCommands { get; } = new();

    /// <summary>Runs on each post -- a test's chance to emit the DocumentChanged the command would raise, or to throw like Revit refusing.</summary>
    public Action<string, FakeUiApplicationAdapter>? OnPostCommand { get; init; }

    public void PostUndo() => Post("undo");

    public void PostRedo() => Post("redo");

    private void Post(string direction)
    {
        PostedCommands.Add(direction);
        OnPostCommand?.Invoke(direction, this);
    }

    public IUiDocumentAdapter? ActiveUiDocument { get; init; }

    private readonly List<Action<DocumentChange>> _changeSubscribers = new();

    /// <summary>How many DocumentChanged subscriptions are live -- the executor must leave zero behind.</summary>
    public int ChangeSubscribers => _changeSubscribers.Count;

    /// <summary>Stands in for Revit raising Application.DocumentChanged (#146 Phase 2).</summary>
    public void EmitChange(DocumentChange change)
    {
        foreach (var subscriber in _changeSubscribers.ToArray())
        {
            subscriber(change);
        }
    }

    /// <summary>Runs when the executor subscribes -- a hook to raise a change "during the run" from a test whose script cannot touch Revit.</summary>
    public Action<FakeUiApplicationAdapter>? OnSubscribed { get; init; }

    public IDisposable Subscribe(Action<DocumentChange> onChange)
    {
        _changeSubscribers.Add(onChange);
        OnSubscribed?.Invoke(this);
        return new Unsubscriber(() => _changeSubscribers.Remove(onChange));
    }

    private sealed class Unsubscriber : IDisposable
    {
        private Action? _dispose;
        public Unsubscriber(Action dispose) => _dispose = dispose;
        public void Dispose()
        {
            _dispose?.Invoke();
            _dispose = null;
        }
    }

    /// <summary>Candidates the routing error reports; empty by default.</summary>
    public IReadOnlyList<OpenDocumentInfo> OpenDocuments { get; init; } = Array.Empty<OpenDocumentInfo>();

    /// <summary>Per-test routing table for FindOpenDocument; null means "nothing else is open".</summary>
    public Func<string, IDocumentAdapter?>? FindOpenDocumentHandler { get; init; }

    public IDocumentAdapter? FindOpenDocument(string documentId) => FindOpenDocumentHandler?.Invoke(documentId);

    /// <summary>The adapter handed back by both creation members; null means "this test never creates one".</summary>
    public IDocumentAdapter? CreatedDocument { get; init; }

    public string? LastProjectTemplatePath { get; private set; }
    public string? LastFamilyTemplatePath { get; private set; }

    public IDocumentAdapter CreateProjectDocument(string? templatePath)
    {
        LastProjectTemplatePath = templatePath;
        return CreatedDocument
            ?? throw new InvalidOperationException(
                "This FakeUiApplicationAdapter was not given a CreatedDocument, so it cannot create one.");
    }

    public IDocumentAdapter CreateFamilyDocument(string templatePath)
    {
        LastFamilyTemplatePath = templatePath;
        return CreatedDocument
            ?? throw new InvalidOperationException(
                "This FakeUiApplicationAdapter was not given a CreatedDocument, so it cannot create one.");
    }
}

/// <summary>See FakeDocumentAdapter: deliberately does not implement IRawUiDocumentSource (PRD §14).</summary>
internal sealed class FakeUiDocumentAdapter : IUiDocumentAdapter
{
    public IDocumentAdapter Document { get; init; } = new FakeDocumentAdapter();
}
