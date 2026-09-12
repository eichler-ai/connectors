using Rhino.MCPBridge.Core.Execution;

namespace Rhino.MCPBridge.Core.Tests.Fakes;

/// <summary>
/// Stands in for Rhino's command loop: runs the body inline (or refuses, to simulate a person
/// mid-command), records undo requests, and lets a test inject document changes during the body.
/// Document is null (see TestGlobals); scripts under test never dereference it.
/// </summary>
internal sealed class FakeRunHost : IRunHost
{
    public string BridgeVersion => "test";
    public bool RefuseCommands { get; set; }
    public bool UndoSucceeds { get; set; } = true;
    public bool LastWasOurs { get; set; } = true;
    public Action<Action<DocumentChange>>? DuringRun { get; set; }
    /// <summary>Fires the "any change" signal during the run without an object event (a layer change, say).</summary>
    public bool NonObjectChangeDuringRun { get; set; }
    public List<string> UndoLabels { get; } = new();
    public int UndoCalls { get; private set; }
    public int CommandsRun { get; private set; }
    public HashSet<string> KnownDocumentIds { get; } = new() { "", "tmp-known" };

    /// <summary>gh_document_ids this fake resolves; a non-empty id not in here reports not-found.</summary>
    public HashSet<string> KnownGrasshopperDocumentIds { get; } = new();

    /// <summary>The opaque GH_Document stand-in a resolved gh_document_id yields (an object in tier 1;
    /// a real GH_Document only lives inside Rhino).</summary>
    public object GrasshopperDocumentStub { get; } = new();

    // Raw is null: a RhinoDoc must never be materialised in tier 1 (see RunDocument's doc).
    public RunDocument? ResolveDocument(string documentId) => KnownDocumentIds.Contains(documentId) ? new RunDocument(documentId.Length == 0 ? "tmp-known" : documentId, raw: null) : null;

    public object? ResolveGrasshopperDocument(string grasshopperDocumentId, out bool notFound)
    {
        notFound = false;
        if (string.IsNullOrEmpty(grasshopperDocumentId)) return null; // none requested
        if (KnownGrasshopperDocumentIds.Contains(grasshopperDocumentId)) return GrasshopperDocumentStub;
        notFound = true;
        return null;
    }

    /// <summary>The solve report this fake yields for the run (null = no solve). Tier 1 has no real
    /// Grasshopper, so the scope just hands back whatever the test set.</summary>
    public GrasshopperReport? GrasshopperReportToReturn { get; set; }

    public IGrasshopperSolveScope BeginGrasshopperSolves(object? grasshopperDocument) =>
        GrasshopperReportToReturn is null ? NullGrasshopperSolveScope.Instance : new StubSolveScope(GrasshopperReportToReturn);

    private sealed class StubSolveScope : IGrasshopperSolveScope
    {
        private readonly GrasshopperReport _report;
        public StubSolveScope(GrasshopperReport report) => _report = report;
        public GrasshopperReport? BuildReport() => _report;
        public void Dispose() { }
    }

    public IReadOnlyList<(string DocumentId, string Title, bool Active)> OpenDocuments() => new[] { ("tmp-known", "Untitled", true) };

    public bool RunInCommand(RunDocument document, string undoLabel, Action body)
    {
        if (RefuseCommands) return false;
        CommandsRun++;
        UndoLabels.Add(undoLabel);
        body();
        return true;
    }

    public bool UndoLast(RunDocument document) { UndoCalls++; return UndoSucceeds; }

    public bool LastCommandWasOurs() => LastWasOurs;
    public int RedoCalls { get; private set; }
    public bool RedoSucceeds { get; set; } = true;
    /// <summary>What LastCommand() reports (named in the undo tool's refusal).</summary>
    public (Guid Id, string Name) Last { get; set; } = (Guid.NewGuid(), "Line");
    public bool RedoLast(RunDocument document) { RedoCalls++; return RedoSucceeds; }
    public (Guid Id, string Name) LastCommand() => Last;

    /// <summary>The global monitor's callback, so a test can report a "person's" change.</summary>
    public Action<string>? Monitor { get; private set; }
    public IDisposable MonitorChanges(Action<string> onChange) { Monitor = onChange; return new Unsub(() => Monitor = null); }

    public IDisposable SubscribeChanges(RunDocument document, Action<DocumentChange> onChange, Action onAnyChange)
    {
        DuringRun?.Invoke(c => { onAnyChange(); onChange(c); });
        if (NonObjectChangeDuringRun) onAnyChange();
        return new Unsub(() => { });
    }

    private sealed class Unsub : IDisposable { private readonly Action _a; public Unsub(Action a) { _a = a; } public void Dispose() => _a(); }
}

internal sealed class InlineLauncher : IRunLauncher
{
    public int Posted { get; private set; }
    public void Post(Action onMainThread) { Posted++; onMainThread(); }
}
