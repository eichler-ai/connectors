using MCPBridge.Core.Execution;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

/// <summary>
/// Mirrors ActiveDialogContextTests -- same shared-static-state posture (ActiveExportContext is a
/// plain static, safe in production per its own doc comment, but every test here must start/end
/// cleared so xUnit's parallel test execution can't leak state between tests).
/// </summary>
public class ActiveExportContextTests
{
    public ActiveExportContextTests()
    {
        ActiveExportContext.ClearActive();
    }

    [Fact]
    public void IsActive_FalseByDefault_TrueAfterSetActive_FalseAfterClear()
    {
        Assert.False(ActiveExportContext.IsActive);

        ActiveExportContext.SetActive(@"C:\exports", overwriteOutputFiles: false);
        Assert.True(ActiveExportContext.IsActive);

        ActiveExportContext.ClearActive();
        Assert.False(ActiveExportContext.IsActive);
    }

    [Fact]
    public void SetActive_ExposesExportsDirectoryAndOverwriteFlag()
    {
        ActiveExportContext.SetActive(@"C:\exports", overwriteOutputFiles: true);

        Assert.Equal(@"C:\exports", ActiveExportContext.ExportsDirectoryPath);
        Assert.True(ActiveExportContext.OverwriteOutputFiles);
    }

    [Fact]
    public void ClearActive_ResetsExportsDirectoryAndOverwriteFlag()
    {
        ActiveExportContext.SetActive(@"C:\exports", overwriteOutputFiles: true);

        ActiveExportContext.ClearActive();

        Assert.Null(ActiveExportContext.ExportsDirectoryPath);
        Assert.False(ActiveExportContext.OverwriteOutputFiles);
    }

    [Fact]
    public void RecordPublished_DrainRecorded_RoundTrips_AndClearsBetweenRuns()
    {
        ActiveExportContext.SetActive(@"C:\exports", overwriteOutputFiles: false);
        var record = new PublishedFileRecord("view.png", @"C:\exports\view.png", PublishedFileRecord.StatusPublished, null);
        ActiveExportContext.RecordPublished(record);

        var drained = ActiveExportContext.DrainRecorded();

        Assert.Single(drained);
        Assert.Same(record, drained[0]);
        Assert.Empty(ActiveExportContext.DrainRecorded()); // draining clears it

        // A fresh SetActive (the next script run) must not inherit anything from a prior run.
        ActiveExportContext.RecordPublished(record);
        ActiveExportContext.SetActive(@"C:\exports2", overwriteOutputFiles: false);
        Assert.Empty(ActiveExportContext.DrainRecorded());
    }
}
