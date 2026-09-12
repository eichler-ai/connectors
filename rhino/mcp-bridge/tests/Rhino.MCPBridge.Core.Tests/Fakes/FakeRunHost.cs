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

    /// <summary>Records the Connector.Grasshopper operations the executor forwarded, so tier 1 can assert
    /// they reached the ops layer with the right document and arguments (no real Grasshopper needed).</summary>
    public FakeGrasshopperOperations GrasshopperOps { get; } = new();

    public IGrasshopperOperations GrasshopperOperations => GrasshopperOps;

    internal sealed class FakeGrasshopperOperations : IGrasshopperOperations
    {
        public readonly List<(object Doc, string Query)> Finds = new();
        public readonly List<(object Doc, string Nickname, object Value)> Sets = new();
        public readonly List<(object Doc, string Nickname, object? ObjectIds)> References = new();
        public readonly List<(object Doc, bool ExpireAll)> Solves = new();
        public readonly List<(object Doc, string Nickname)> Gets = new();
        public readonly List<(object Doc, string Nickname)> Datas = new();
        public Eichler.Connectors.Rhino.GrasshopperComponent? FindResult { get; set; }
        public Eichler.Connectors.Rhino.GrasshopperValue? GetResult { get; set; }
        public Eichler.Connectors.Rhino.GrasshopperData? DataResult { get; set; }

        public Eichler.Connectors.Rhino.GrasshopperComponent? Find(object doc, string q) { Finds.Add((doc, q)); return FindResult; }
        public void Set(object doc, string nickname, object value) => Sets.Add((doc, nickname, value));
        public void Reference(object doc, string nickname, object? objectIds) => References.Add((doc, nickname, objectIds));
        public void Solve(object doc, bool expireAll) => Solves.Add((doc, expireAll));
        public Eichler.Connectors.Rhino.GrasshopperValue Get(object doc, string nickname) { Gets.Add((doc, nickname)); return GetResult!; }
        public Eichler.Connectors.Rhino.GrasshopperData Data(object doc, string nickname) { Datas.Add((doc, nickname)); return DataResult!; }
    }

    private sealed class StubSolveScope : IGrasshopperSolveScope
    {
        private readonly GrasshopperReport _report;
        public StubSolveScope(GrasshopperReport report) => _report = report;
        public GrasshopperReport? BuildReport() => _report;
        public void Dispose() { }
    }

    public IReadOnlyList<(string DocumentId, string Title, bool Active)> OpenDocuments() => new[] { ("tmp-known", "Untitled", true) };

    /// <summary>The save states restart_snapshot returns; a test sets these to simulate saved/unsaved docs.</summary>
    public List<DocumentSaveState> SaveStates { get; } = new();
    public IReadOnlyList<DocumentSaveState> RestartSaveStates() => SaveStates;

    /// <summary>inspect_gh_definition's result and error control. Non-null <see cref="InspectResult"/> is a
    /// success; null with <see cref="InspectNotFound"/> true is definition-not-found; null with it false is
    /// grasshopper-not-loaded — the two null paths the dispatcher distinguishes.</summary>
    public GrasshopperDefinitionInfo? InspectResult { get; set; }
    public bool InspectNotFound { get; set; }
    public readonly List<(string Id, string? Filter, int Offset, int Limit)> Inspects = new();
    public GrasshopperDefinitionInfo? InspectGrasshopperDefinition(string grasshopperDocumentId, string? nameFilter, int offset, int limit, out bool notFound)
    {
        Inspects.Add((grasshopperDocumentId, nameFilter, offset, limit));
        notFound = InspectNotFound;
        return InspectResult;
    }

    /// <summary>The canvas image capture_view's "canvas" target returns; null simulates no open canvas.</summary>
    public GrasshopperCanvasImage? CanvasImage { get; set; }
    public readonly List<(string Mime, bool Transparent, int Width, int Height)> CanvasCaptures = new();
    public GrasshopperCanvasImage? CaptureGrasshopperCanvas(string mimeType, bool transparent, int requestedWidth, int requestedHeight)
    {
        CanvasCaptures.Add((mimeType, transparent, requestedWidth, requestedHeight));
        return CanvasImage;
    }

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
