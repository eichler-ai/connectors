using System.Collections.Generic;
using MCPBridge.Core.Diagnostics;
using MCPBridge.Core.Execution;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

/// <summary>
/// Covers the IsActive gate directly (review finding: DialogSuppressionHandler must not auto-answer
/// dialogs while no script is running -- IsActive is what it checks). The handler itself lives in
/// MCPBridge.AddIn and needs real Revit event args, so it's only live-testable; this is the pure-logic
/// slice that is testable here.
/// </summary>
[Collection(ActiveDialogContextCollection.Name)]
public class ActiveDialogContextTests
{
    public ActiveDialogContextTests()
    {
        // These tests share process-wide static state with every other test in this assembly (by
        // design -- see ActiveDialogContext's own doc comment on why a plain static is safe in
        // production). Start and end each test cleared so xUnit's parallel test execution can't leak
        // state between them.
        ActiveDialogContext.ClearActive();
    }

    [Fact]
    public void IsActive_FalseByDefault_TrueAfterSetActive_FalseAfterClear()
    {
        Assert.False(ActiveDialogContext.IsActive);

        ActiveDialogContext.SetActive(new Dictionary<string, int>());
        Assert.True(ActiveDialogContext.IsActive);

        ActiveDialogContext.ClearActive();
        Assert.False(ActiveDialogContext.IsActive);
    }

    [Fact]
    public void TryGetOverride_ReturnsScriptOverride_NullWhenAbsent()
    {
        ActiveDialogContext.SetActive(new Dictionary<string, int> { ["dlg-1"] = 42 });

        Assert.Equal(42, ActiveDialogContext.TryGetOverride("dlg-1"));
        Assert.Null(ActiveDialogContext.TryGetOverride("dlg-unknown"));
    }

    [Fact]
    public void RecordShown_DrainRecorded_RoundTrips_AndClearsBetweenRuns()
    {
        ActiveDialogContext.SetActive(new Dictionary<string, int>());
        var notice = DiagnosticRecord.Create(DiagnosticSeverity.Info, "dialog-auto-answered", DiagnosticSource.Dialogs, "a dialog fired.", null, null);
        ActiveDialogContext.RecordShown(notice);

        var drained = ActiveDialogContext.DrainRecorded();

        Assert.Single(drained);
        Assert.Empty(ActiveDialogContext.DrainRecorded()); // draining clears it

        // A fresh SetActive (the next script run) must not inherit anything from a prior run.
        ActiveDialogContext.RecordShown(notice);
        ActiveDialogContext.SetActive(new Dictionary<string, int>());
        Assert.Empty(ActiveDialogContext.DrainRecorded());
    }
}
