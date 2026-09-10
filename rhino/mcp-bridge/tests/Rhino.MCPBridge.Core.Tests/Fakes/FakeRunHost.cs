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
    public string? RecentCommand { get; set; } = UndoRunExecutor.RunCommandName;
    public Action<Action<DocumentChange>>? DuringRun { get; set; }
    public List<string> UndoLabels { get; } = new();
    public int UndoCalls { get; private set; }
    public int CommandsRun { get; private set; }
    public HashSet<string> KnownDocumentIds { get; } = new() { "", "tmp-known" };

    // Raw is null: a RhinoDoc must never be materialised in tier 1 (see RunDocument's doc).
    public RunDocument? ResolveDocument(string documentId) => KnownDocumentIds.Contains(documentId) ? new RunDocument(documentId.Length == 0 ? "tmp-known" : documentId, raw: null) : null;

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

    public string? MostRecentCommandName() => RecentCommand;

    private Action<DocumentChange>? _onChange;
    public IDisposable SubscribeChanges(RunDocument document, Action<DocumentChange> onChange)
    {
        _onChange = onChange;
        DuringRun?.Invoke(onChange);
        return new Unsub(() => _onChange = null);
    }

    private sealed class Unsub : IDisposable { private readonly Action _a; public Unsub(Action a) { _a = a; } public void Dispose() => _a(); }
}

internal sealed class InlineLauncher : IRunLauncher
{
    public int Posted { get; private set; }
    public void Post(Action onMainThread) { Posted++; onMainThread(); }
}
