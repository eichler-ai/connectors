using Rhino.MCPBridge.Core.Execution;
using Rhino.MCPBridge.Core.Tests.Fakes;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Execution;

/// <summary>PRD §07 with the host faked: one command per run, undo after a failure, and the three
/// rollback outcomes reported as records -- never silently.</summary>
public sealed class UndoRunExecutorTests
{
    private static readonly RoslynScriptRunner Runner = new();

    private static UndoRunExecutor.Request Req(string script, string docId = "", string? label = null, CancellationToken ct = default, string ghDocId = "") => new()
    {
        ExecutionId = "exec-1", ScriptText = script, Language = "csharp", DocumentId = docId, GrasshopperDocumentId = ghDocId, CancellationToken = ct, Label = label,
    };

    private static void OneAdd(Action<DocumentChange> on) => on(new DocumentChange(DocumentChange.Kind.Added, Guid.NewGuid(), "Brep", "Default"));

    [Fact]
    public void Success_RunsInOneCommand_WithTheLabel_AndReportsMutations()
    {
        var host = new FakeRunHost { DuringRun = OneAdd };
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host).Execute(Req("return 42;", label: "make things"))!;
        Assert.True(outcome.Success, outcome.Exception?.ToString());
        Assert.Equal(42, outcome.ReturnValue);
        Assert.Equal(1, host.CommandsRun);
        Assert.Equal(new[] { "MCP: make things" }, host.UndoLabels);
        Assert.Equal(0, host.UndoCalls);
        Assert.Equal(1, outcome.Mutations!.NetAdded);
    }

    [Fact]
    public void ReadOnlySuccess_HasNoMutationReport()
    {
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), new FakeRunHost()).Execute(Req("return 1;"))!;
        Assert.True(outcome.Success);
        Assert.Null(outcome.Mutations);
    }

    [Fact]
    public void Throw_AfterChanging_IsUndone_AndReported()
    {
        var host = new FakeRunHost { DuringRun = OneAdd };
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host).Execute(Req("throw new System.InvalidOperationException(\"boom\");"))!;
        Assert.False(outcome.Success);
        Assert.Equal(1, host.UndoCalls);
        var notice = Assert.Single(outcome.Notices);
        Assert.Equal("script-rolled-back", notice.Code);
        Assert.Contains("1 added", notice.Message);
    }

    [Fact]
    public void Throw_WithoutChanging_DoesNotUndo()
    {
        var host = new FakeRunHost();
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host).Execute(Req("throw new System.Exception(\"x\");"))!;
        Assert.False(outcome.Success);
        Assert.Equal(0, host.UndoCalls);
        Assert.Empty(outcome.Notices);
    }

    [Fact]
    public void Rollback_IsSkipped_WhenAnotherCommandIsMostRecent()
    {
        // A person acted between the run and the rollback: never revert their action (PRD §07).
        var host = new FakeRunHost { DuringRun = OneAdd, LastWasOurs = false };
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host).Execute(Req("throw new System.Exception(\"x\");"))!;
        Assert.Equal(0, host.UndoCalls);
        var notice = Assert.Single(outcome.Notices);
        Assert.Equal("script-rollback-skipped", notice.Code);
        Assert.Contains("not the connector's", notice.Message);
    }

    [Fact]
    public void Throw_AfterANonObjectChange_IsStillUndone()
    {
        // review of #282: a layer or attribute change is a change; the rollback keys off ANY document
        // event, not off the object counts the report can net.
        var host = new FakeRunHost { NonObjectChangeDuringRun = true };
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host).Execute(Req("throw new System.Exception(\"x\");"))!;
        Assert.Equal(1, host.UndoCalls);
        Assert.Equal("script-rolled-back", Assert.Single(outcome.Notices).Code);
        Assert.Null(outcome.Mutations);
    }

    [Fact]
    public void AnExceptionInsideTheCommandBody_BecomesAFailedOutcome_NeverEscapes()
    {
        // review of #282: the body runs inside Rhino's native command dispatcher; a throw there is a crash class.
        var host = new FakeRunHost { DuringRun = _ => throw new InvalidOperationException("subscribe exploded") };
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host).Execute(Req("return 1;"))!;
        Assert.False(outcome.Success);
        Assert.Contains("subscribe exploded", outcome.Exception!.Message);
    }

    [Fact]
    public void Rollback_ReportsWhenRhinoHadNothingToUndo()
    {
        var host = new FakeRunHost { DuringRun = OneAdd, UndoSucceeds = false };
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host).Execute(Req("throw new System.Exception(\"x\");"))!;
        Assert.Equal(1, host.UndoCalls);
        Assert.Equal("script-rollback-skipped", Assert.Single(outcome.Notices).Code);
    }

    [Fact]
    public void Cancellation_IsUndone_AndStaysCancelled()
    {
        using var cts = new CancellationTokenSource();
        var host = new FakeRunHost { DuringRun = on => { OneAdd(on); cts.Cancel(); } };
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host).Execute(Req("CancellationToken.ThrowIfCancellationRequested(); return 1;", ct: cts.Token))!;
        Assert.True(outcome.WasCancelled);
        Assert.Equal(1, host.UndoCalls);
        Assert.Equal("script-rolled-back", Assert.Single(outcome.Notices).Code);
    }

    [Fact]
    public void UnknownDocument_FailsWithCandidates_WithoutRunningACommand()
    {
        var host = new FakeRunHost();
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host).Execute(Req("return 1;", docId: "doc-nope"))!;
        Assert.False(outcome.Success);
        var ex = Assert.IsType<DocumentNotFoundException>(outcome.Exception);
        Assert.Equal("document-not-found", ex.Record.Code);
        Assert.Equal(0, host.CommandsRun);
    }

    [Fact]
    public void RhinoBusyWithAnotherCommand_ReturnsNull_ForTheLauncherToRetry()
    {
        var host = new FakeRunHost { RefuseCommands = true };
        Assert.Null(new UndoRunExecutor(new ScriptRunners(Runner), host).Execute(Req("return 1;")));
    }

    [Theory]
    [InlineData(null, "MCP Bridge Script")]
    [InlineData("  ", "MCP Bridge Script")]
    [InlineData("create\nwalls\ton L1", "MCP: create walls on L1")]
    public void UndoLabel_IsSanitised(string? label, string expected) => Assert.Equal(expected, UndoLabel.For(label));

    [Fact]
    public void UndoLabel_IsCapped()
    {
        var l = UndoLabel.For(new string('x', 200));
        Assert.True(l.Length <= "MCP: ".Length + UndoLabel.MaxLength);
    }

    // ----- gh_document_id -> GrasshopperDocument global (PRD §10, phase 4 PR2) -----

    [Fact]
    public void OmittedGrasshopperDocumentId_LeavesTheGlobalNull()
    {
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), new FakeRunHost())
            .Execute(Req("return GrasshopperDocument == null ? \"null\" : \"set\";"))!;
        Assert.Equal("null", outcome.ReturnValue);
    }

    [Fact]
    public void ResolvedGrasshopperDocumentId_ReachesTheScriptGlobal()
    {
        var host = new FakeRunHost();
        host.KnownGrasshopperDocumentIds.Add("gh-known");
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host)
            .Execute(Req("return GrasshopperDocument == null ? \"null\" : \"set\";", ghDocId: "gh-known"))!;
        Assert.Equal("set", outcome.ReturnValue);
    }

    [Fact]
    public void UnknownGrasshopperDocumentId_FailsWithGrasshopperDocumentNotFound_BeforeRunning()
    {
        var host = new FakeRunHost();
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host)
            .Execute(Req("return 1;", ghDocId: "gh-nope"))!;
        var gnf = Assert.IsType<GrasshopperDocumentNotFoundException>(outcome.Exception);
        Assert.Equal("grasshopper-document-not-found", gnf.Record.Code);
        Assert.Equal(0, host.CommandsRun); // refused before the run command started
    }

    [Fact]
    public void GlobalNames_IncludesGrasshopperDocument()
    {
        Assert.Contains("GrasshopperDocument", ScriptGlobals.GlobalNames);
    }

    // ----- Grasshopper solve report (PRD §10, phase 4 PR3) -----

    private static GrasshopperReport SampleReport() => new()
    {
        Solutions = new[] { new GrasshopperSolution { StartedAt = "t", DurationMs = 1.0, State = "Process", Depth = 0 } },
        Components = new[] { new GrasshopperComponentReport { Guid = Guid.NewGuid(), Nickname = "Slider", Type = "Number Slider", Phase = "Failed", ProcessorMs = 0.5, Messages = new[] { new GrasshopperMessage { Severity = "error", Text = "boom" } } } },
    };

    [Fact]
    public void NoSolve_LeavesGrasshopperReportNull()
    {
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), new FakeRunHost()).Execute(Req("return 1;"))!;
        Assert.Null(outcome.Grasshopper);
    }

    [Fact]
    public void GrasshopperSolveReport_ReachesTheOutcome_OnSuccess()
    {
        var host = new FakeRunHost { GrasshopperReportToReturn = SampleReport() };
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host).Execute(Req("return 1;"))!;
        Assert.True(outcome.Success);
        Assert.NotNull(outcome.Grasshopper);
        Assert.Single(outcome.Grasshopper!.Solutions);
        Assert.Equal("Failed", Assert.Single(outcome.Grasshopper.Components).Phase);
    }

    [Fact]
    public void GrasshopperSolveReport_ReachesTheOutcome_EvenOnFailure()
    {
        // Errors are diagnostics, not rolled-back writes: a failed run that solved still reports it (PRD §10).
        var host = new FakeRunHost { GrasshopperReportToReturn = SampleReport() };
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host).Execute(Req("throw new System.Exception(\"x\");"))!;
        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Grasshopper);
    }

    // ----- Connector.Grasshopper forwarding (PRD §10, phase 4 PR4) -----

    private static FakeRunHost HostWithBoundDefinition()
    {
        var host = new FakeRunHost();
        host.KnownGrasshopperDocumentIds.Add("gh-known");
        return host;
    }

    [Fact]
    public void ConnectorGrasshopperSet_ForwardsToTheOps_WithTheBoundDefinition()
    {
        var host = HostWithBoundDefinition();
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host)
            .Execute(Req("Connector.Grasshopper.Set(\"slider\", 5); return 1;", ghDocId: "gh-known"))!;
        Assert.True(outcome.Success, outcome.Exception?.ToString());
        var set = Assert.Single(host.GrasshopperOps.Sets);
        Assert.Same(host.GrasshopperDocumentStub, set.Doc);
        Assert.Equal("slider", set.Nickname);
        Assert.Equal(5, set.Value);
    }

    [Fact]
    public void ConnectorGrasshopperReferenceAndClear_Forward()
    {
        var host = HostWithBoundDefinition();
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host)
            .Execute(Req("Connector.Grasshopper.Reference(\"crv\", \"abc\"); Connector.Grasshopper.ClearReference(\"crv\"); return 1;", ghDocId: "gh-known"))!;
        Assert.True(outcome.Success, outcome.Exception?.ToString());
        Assert.Equal(2, host.GrasshopperOps.References.Count);
        Assert.Equal(("crv", (object?)"abc"), (host.GrasshopperOps.References[0].Nickname, host.GrasshopperOps.References[0].ObjectIds));
        Assert.Null(host.GrasshopperOps.References[1].ObjectIds); // ClearReference passes null
    }

    [Fact]
    public void ConnectorGrasshopperSolve_ForwardsExpireAll()
    {
        var host = HostWithBoundDefinition();
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host)
            .Execute(Req("Connector.Grasshopper.Solve(true); return 1;", ghDocId: "gh-known"))!;
        Assert.True(outcome.Success, outcome.Exception?.ToString());
        Assert.True(Assert.Single(host.GrasshopperOps.Solves).ExpireAll);
    }

    [Fact]
    public void ConnectorGrasshopperFind_ReturnsTheDescriptor()
    {
        var host = HostWithBoundDefinition();
        host.GrasshopperOps.FindResult = new Eichler.Connectors.Rhino.GrasshopperComponent("g", "slider", "Number Slider");
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host)
            .Execute(Req("var c = Connector.Grasshopper.Find(\"slider\"); return c == null ? \"none\" : c.Nickname;", ghDocId: "gh-known"))!;
        Assert.Equal("slider", outcome.ReturnValue);
    }

    [Fact]
    public void ConnectorGrasshopperGet_ForwardsAndReturnsTheValue()
    {
        var host = HostWithBoundDefinition();
        var item = new Eichler.Connectors.Rhino.GrasshopperItem("number", 7.0, "Number", null, null);
        host.GrasshopperOps.GetResult = new Eichler.Connectors.Rhino.GrasshopperValue("slider", "Number Slider", 1, new[] { item }, false);
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host)
            .Execute(Req("var v = Connector.Grasshopper.Get(\"slider\"); return v.Type + \":\" + v.Items[0].Value;", ghDocId: "gh-known"))!;
        Assert.True(outcome.Success, outcome.Exception?.ToString());
        Assert.Equal("Number Slider:7", outcome.ReturnValue);
        var get = Assert.Single(host.GrasshopperOps.Gets);
        Assert.Same(host.GrasshopperDocumentStub, get.Doc);
        Assert.Equal("slider", get.Nickname);
    }

    [Fact]
    public void ConnectorGrasshopperData_ForwardsAndReturnsTheTree()
    {
        var host = HostWithBoundDefinition();
        var branch = new Eichler.Connectors.Rhino.GrasshopperBranch("{0;0}", 1,
            new[] { new Eichler.Connectors.Rhino.GrasshopperItem("geometry", null, "Curve", new[] { 0.0, 0, 0, 10, 0, 0 }, "the-id") });
        host.GrasshopperOps.DataResult = new Eichler.Connectors.Rhino.GrasshopperData("crv", "Curve", 1, 1, new[] { branch }, false, null);
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host)
            .Execute(Req("var d = Connector.Grasshopper.Data(\"crv\"); return d.BranchCount + \":\" + d.Branches[0].Items[0].Handle;", ghDocId: "gh-known"))!;
        Assert.True(outcome.Success, outcome.Exception?.ToString());
        Assert.Equal("1:the-id", outcome.ReturnValue);
        var d = Assert.Single(host.GrasshopperOps.Datas);
        Assert.Same(host.GrasshopperDocumentStub, d.Doc);
        Assert.Equal("crv", d.Nickname);
    }

    [Fact]
    public void ConnectorGrasshopper_WithoutABoundDefinition_FailsClearly_WithoutRunningTheOp()
    {
        var host = new FakeRunHost();
        var outcome = new UndoRunExecutor(new ScriptRunners(Runner), host)
            .Execute(Req("Connector.Grasshopper.Solve(); return 1;"))!; // no gh_document_id
        Assert.False(outcome.Success);
        Assert.Contains("no Grasshopper definition is bound", outcome.Exception!.Message);
        Assert.Empty(host.GrasshopperOps.Solves);
    }
}
