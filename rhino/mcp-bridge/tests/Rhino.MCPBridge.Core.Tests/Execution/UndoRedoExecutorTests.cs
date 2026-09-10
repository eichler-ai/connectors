using Rhino.MCPBridge.Core.Diagnostics;
using Rhino.MCPBridge.Core.Execution;
using Rhino.MCPBridge.Core.Tests.Fakes;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Execution;

/// <summary>The undo/redo tools' gate-by-evidence (PRD §07) over the fake host and the change clock:
/// the connector's own completed runs and tool operations against document changes made outside them.</summary>
public sealed class UndoRedoExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    private const string Doc = "tmp-known";

    private sealed class World
    {
        public FakeRunHost Host { get; } = new() { Last = (Guid.NewGuid(), "Line") };
        public RunLedger Ledger { get; } = new();
        public ChangeClock Clock { get; } = new();
        public UndoRedoExecutor Exec { get; }
        public World() { Exec = new UndoRedoExecutor(Host, Ledger, Clock); }

        /// <summary>A completed connector run on the document, as the dispatcher's Finish records it.</summary>
        public void OurRun(bool changed, string id = "exec-9", string? label = "make box") =>
            Ledger.Record(Doc, new LastRun { ExecutionId = id, Label = label, FinishedAt = "t", AgentClientId = "srv-a", ChangedDocument = changed, Tick = Clock.Next() });

        /// <summary>A person's change: the adapter's monitor reports it outside connector work.</summary>
        public void PersonChanges() => Clock.NoteChange(Doc);

        public UndoRedoExecutor.Outcome Undo(bool confirm = false) => Exec.Execute(UndoRedoExecutor.Direction.Undo, "", confirm, "u", Now);
        public UndoRedoExecutor.Outcome Redo(bool confirm = false) => Exec.Execute(UndoRedoExecutor.Direction.Redo, "", confirm, "r", Now);
    }

    [Fact]
    public void UndoOfTheConnectorsOwnRun_NeedsNoConfirm_NamesTheRun_AndReportsTheReversal()
    {
        var w = new World();
        w.OurRun(changed: true);
        w.Host.DuringRun = on => on(new DocumentChange(DocumentChange.Kind.Deleted, Guid.NewGuid(), "Brep", "Default"));
        var outcome = w.Undo();
        Assert.Null(outcome.Error);
        var notice = Assert.Single(outcome.Notices);
        Assert.Equal("undo-reverted-connector-work", notice.Code);
        Assert.Contains("exec-9", notice.Message);
        Assert.Contains("make box", notice.Message);
        Assert.Equal(1, w.Host.UndoCalls);
        Assert.Equal(1, outcome.Mutations!.NetDeleted);
    }

    [Fact]
    public void AReadOnlyRunAfterOurChange_DoesNotSpoilTheEvidence()
    {
        // The live finding behind the clock: a read-only run leaves MCPBridgeRun as Rhino's last
        // command but adds no undo entry; the top is still our mutating run.
        var w = new World();
        w.OurRun(changed: true, id: "exec-1");
        w.OurRun(changed: false, id: "exec-2", label: null); // objectNames-style read
        var outcome = w.Undo();
        Assert.Null(outcome.Error);
        Assert.Equal("undo-reverted-connector-work", outcome.Notices[0].Code);
    }

    [Fact]
    public void APersonsChangeAfterOurRun_RequiresConfirm_NamingTheLastCommand_AndRunsWithIt()
    {
        var w = new World();
        w.OurRun(changed: true);
        w.PersonChanges();
        w.Host.Last = (Guid.NewGuid(), "Circle");
        var refused = w.Undo();
        Assert.Equal("undo-confirmation-required", refused.Error!.Code);
        Assert.Contains("'Circle'", refused.Error.Message);
        Assert.Equal(0, w.Host.UndoCalls);

        var forced = w.Undo(confirm: true);
        Assert.Null(forced.Error);
        Assert.Equal("undo-reverted-other-work", forced.Notices[0].Code);
        Assert.Equal(DiagnosticSeverity.Warning, forced.Notices[0].Severity);
        Assert.Equal(1, w.Host.UndoCalls);
    }

    [Fact]
    public void ChangesDuringConnectorWork_AreNotForeign()
    {
        var w = new World();
        using (w.Clock.EnterConnectorWork())
        {
            w.Clock.NoteChange(Doc); // the run's own events arrive through the same monitor
        }

        w.OurRun(changed: true);
        Assert.Null(w.Undo().Error);
    }

    [Fact]
    public void RedoRightAfterOurUndo_IsSafe_ThenUndoAfterOurRedo_IsSafe_ButNotAcrossAPersonsChange()
    {
        var w = new World();
        w.OurRun(changed: true);
        Assert.Null(w.Undo().Error);
        var redo = w.Redo();
        Assert.Null(redo.Error);
        Assert.Equal("undo-reverted-connector-work", redo.Notices[0].Code);
        Assert.Null(w.Undo().Error); // after our redo the top is our run again
        Assert.Null(w.Redo().Error);
        w.PersonChanges();
        Assert.Equal("undo-confirmation-required", w.Undo().Error!.Code);
        Assert.Equal("undo-confirmation-required", w.Redo().Error!.Code);
    }

    [Fact]
    public void ASecondUndo_IsNotSafe_TheEntryBelowIsUnknown()
    {
        var w = new World();
        w.OurRun(changed: true);
        Assert.Null(w.Undo().Error);
        Assert.Equal("undo-confirmation-required", w.Undo().Error!.Code);
    }

    [Fact]
    public void AConnectorRunThatChangesTheDocumentAfterOurUndo_MakesRedoUnsafe()
    {
        var w = new World();
        w.OurRun(changed: true, id: "exec-1");
        Assert.Null(w.Undo().Error);
        w.OurRun(changed: true, id: "exec-2"); // Rhino has emptied the redo stack
        Assert.Equal("undo-confirmation-required", w.Redo().Error!.Code);
        Assert.Null(w.Undo().Error); // but undoing exec-2 is fine
    }

    [Fact]
    public void ARunThatChangedNothing_LeavesNothingSafeToUndo()
    {
        var w = new World();
        w.OurRun(changed: false);
        Assert.Equal("undo-confirmation-required", w.Undo().Error!.Code);
    }

    [Fact]
    public void NothingToUndo_IsItsOwnError()
    {
        var w = new World();
        w.OurRun(changed: true);
        w.Host.UndoSucceeds = false;
        Assert.Equal("undo-nothing-to-undo", w.Undo().Error!.Code);
    }

    [Fact]
    public void UnknownDocument_IsDocumentNotFound()
    {
        var w = new World();
        Assert.Equal("document-not-found", w.Exec.Execute(UndoRedoExecutor.Direction.Undo, "doc-nope", false, "u", Now).Error!.Code);
    }
}
