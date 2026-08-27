using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Tests.Fakes;
using MCPBridge.RevitAdapter;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

public class TransactionScriptExecutorTests
{
    private static TransactionScriptExecutor NewExecutor() => new(new RoslynScriptRunner());

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcpbridge-publish-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task SuccessfulScript_CommitsTransaction_AndAssimilatesGroup()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();

        var outcome = await executor.ExecuteAsync(document, uiApp, null, "1 + 1", CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(new[] { "Start", "Commit" }, document.LastTransaction!.Calls);
        Assert.Equal(new[] { "Start", "Assimilate" }, document.LastTransactionGroup!.Calls);
    }

    [Fact]
    public async Task ThrowingScript_RollsBackTransaction_AndGroup_NeverCommits()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();

        var outcome = await executor.ExecuteAsync(
            document, uiApp, null, "throw new System.InvalidOperationException(\"boom\");", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(new[] { "Start", "RollBack" }, document.LastTransaction!.Calls);
        Assert.Equal(new[] { "Start", "RollBack" }, document.LastTransactionGroup!.Calls);
    }

    [Fact]
    public async Task CommitFailure_RollsBackTransactionAndGroup_ReportsFailure()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        // Force the commit itself to fail after a successful script run.
        var transaction = (FakeTransactionAdapter)document.CreateTransaction("pre-created");
        transaction.ThrowOnCommit = true;

        // Re-point the document's next CreateTransaction call to return our rigged fake.
        var riggedDocument = new RiggedDocumentAdapter(document, transaction);

        var outcome = await executor.ExecuteAsync(riggedDocument, uiApp, null, "1 + 1", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(new[] { "Start", "Commit", "RollBack" }, transaction.Calls);
        Assert.Equal(new[] { "Start", "RollBack" }, riggedDocument.LastTransactionGroup!.Calls);
    }

    [Fact]
    public async Task CancelledScript_RollsBackCleanly()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await executor.ExecuteAsync(
            document, uiApp, null, "CancellationToken.ThrowIfCancellationRequested(); 1", cts.Token);

        Assert.False(outcome.Success);
        Assert.True(outcome.WasCancelled);
        Assert.Equal(new[] { "Start", "RollBack" }, document.LastTransaction!.Calls);
        Assert.Equal(new[] { "Start", "RollBack" }, document.LastTransactionGroup!.Calls);
    }

    [Fact]
    public async Task WarningOnlyFailure_CommitsAndReportsNotice()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var transaction = (FakeTransactionAdapter)document.CreateTransaction("pre-created");
        transaction.FailuresToReport = new[] { new FailureSummary(false, "wall is slightly off axis", "warn-def-1", System.Array.Empty<string>()) };
        var riggedDocument = new RiggedDocumentAdapter(document, transaction);

        var outcome = await executor.ExecuteAsync(riggedDocument, uiApp, null, "1 + 1", CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(new[] { "Start", "Commit" }, transaction.Calls);
        Assert.Equal(new[] { "Start", "Assimilate" }, riggedDocument.LastTransactionGroup!.Calls);
        Assert.Contains(outcome.Notices, n => n.Message.Contains("off axis"));
    }

    [Fact]
    public async Task ErrorFailure_RollsBackGroupOnly_NotTransactionAgain_ReturnsFailedWithNotices()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var transaction = (FakeTransactionAdapter)document.CreateTransaction("pre-created");
        transaction.FailuresToReport = new[] { new FailureSummary(true, "elements would be deleted", "err-def-1", System.Array.Empty<string>()) };
        var riggedDocument = new RiggedDocumentAdapter(document, transaction);

        var outcome = await executor.ExecuteAsync(riggedDocument, uiApp, null, "1 + 1", CancellationToken.None);

        Assert.False(outcome.Success);
        // Commit() already rolled the Transaction back internally (ProceedWithRollBack) -- only one
        // "Commit" call, no separate "RollBack" on the transaction itself.
        Assert.Equal(new[] { "Start", "Commit" }, transaction.Calls);
        Assert.Equal(new[] { "Start", "RollBack" }, riggedDocument.LastTransactionGroup!.Calls);
        Assert.Contains(outcome.Notices, n => n.Message.Contains("deleted"));
    }

    // --- PRD §09: Publish / files[] ---

    [Fact]
    public async Task ScriptThatPublishes_Succeeds_RecordsPublishedFile()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var sourcePath = Path.Combine(tempDir, "source.txt");
            File.WriteAllText(sourcePath, "hello");
            var exportsDir = Path.Combine(tempDir, "exports");
            Directory.CreateDirectory(exportsDir);

            var script = $"Publish(@\"{sourcePath}\");";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, overwriteOutputFiles: false);

            Assert.True(outcome.Success);
            var published = Assert.Single(outcome.Files);
            Assert.Equal(PublishedFileRecord.StatusPublished, published.Status);
            Assert.True(File.Exists(Path.Combine(exportsDir, "source.txt")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishCollision_OverwriteFalse_RecordsFailedNamingTheFlag_ScriptOutcomeUnaffected()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var sourcePath = Path.Combine(tempDir, "source.txt");
            File.WriteAllText(sourcePath, "hello");
            var exportsDir = Path.Combine(tempDir, "exports");
            Directory.CreateDirectory(exportsDir);
            var destinationPath = Path.Combine(exportsDir, "source.txt");
            File.WriteAllText(destinationPath, "existing");

            var script = $"Publish(@\"{sourcePath}\");";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, overwriteOutputFiles: false);

            Assert.True(outcome.Success); // a publish failure never rolls back or fails the script's own outcome
            var failed = Assert.Single(outcome.Files);
            Assert.Equal(PublishedFileRecord.StatusFailed, failed.Status);
            Assert.Contains("overwrite_output_files", failed.Message);
            Assert.Equal("existing", File.ReadAllText(destinationPath)); // untouched
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishCollision_OverwriteTrue_Succeeds_ReplacesDestination()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var sourcePath = Path.Combine(tempDir, "source.txt");
            File.WriteAllText(sourcePath, "new content");
            var exportsDir = Path.Combine(tempDir, "exports");
            Directory.CreateDirectory(exportsDir);
            var destinationPath = Path.Combine(exportsDir, "source.txt");
            File.WriteAllText(destinationPath, "existing");

            var script = $"Publish(@\"{sourcePath}\");";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, overwriteOutputFiles: true);

            Assert.True(outcome.Success);
            var published = Assert.Single(outcome.Files);
            Assert.Equal(PublishedFileRecord.StatusPublished, published.Status);
            Assert.Equal("new content", File.ReadAllText(destinationPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ScriptThatPublishesThenThrows_StillReportsPublishedFile()
    {
        // PRD §09 invariant: files[] is never conditional on the run's own outcome.
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var sourcePath = Path.Combine(tempDir, "source.txt");
            File.WriteAllText(sourcePath, "hello");
            var exportsDir = Path.Combine(tempDir, "exports");
            Directory.CreateDirectory(exportsDir);

            var script = $"Publish(@\"{sourcePath}\"); throw new System.InvalidOperationException(\"boom\");";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, overwriteOutputFiles: false);

            Assert.False(outcome.Success);
            var published = Assert.Single(outcome.Files);
            Assert.Equal(PublishedFileRecord.StatusPublished, published.Status);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ScriptThatPublishesThenIsCancelled_StillReportsPublishedFile()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var sourcePath = Path.Combine(tempDir, "source.txt");
            File.WriteAllText(sourcePath, "hello");
            var exportsDir = Path.Combine(tempDir, "exports");
            Directory.CreateDirectory(exportsDir);

            // The token is deliberately NOT pre-cancelled here: RoslynScriptRunner.RunAsync checks
            // cancellationToken.ThrowIfCancellationRequested() before the script body ever runs (see its
            // own source), so a pre-cancelled token would never let Publish() execute at all -- that would
            // test nothing about the files[]-survives-cancellation invariant this test exists to cover.
            // Instead, the script itself calls Publish() and then throws OperationCanceledException
            // directly, which RunAsync catches and reports as WasCancelled -- exercising the exact
            // "published, then the run resolved to cancelled" sequence PRD §09's invariant describes,
            // without depending on a real mid-run cooperative-cancellation race.
            var script = $"Publish(@\"{sourcePath}\"); throw new System.OperationCanceledException();";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, overwriteOutputFiles: false);

            Assert.True(outcome.WasCancelled);
            var published = Assert.Single(outcome.Files);
            Assert.Equal(PublishedFileRecord.StatusPublished, published.Status);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ScriptThatDoesNotPublish_HasEmptyFilesArray_NoExportsDirectoryNeeded()
    {
        // exportsDirectoryPath omitted entirely -- existing callers/tests that don't pass one must
        // keep working unaffected by this feature.
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();

        var outcome = await executor.ExecuteAsync(document, uiApp, null, "1 + 1", CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Empty(outcome.Files);
    }

    /// <summary>Test-only helper: a document adapter that hands out a pre-built (rigged) transaction instead of a fresh one.</summary>
    private sealed class RiggedDocumentAdapter : MCPBridge.RevitAdapter.IDocumentAdapter
    {
        private readonly FakeDocumentAdapter _inner;
        private readonly FakeTransactionAdapter _riggedTransaction;

        public RiggedDocumentAdapter(FakeDocumentAdapter inner, FakeTransactionAdapter riggedTransaction)
        {
            _inner = inner;
            _riggedTransaction = riggedTransaction;
        }

        public string Title => _inner.Title;
        public string? PathName => _inner.PathName;
        public bool IsWorkshared => _inner.IsWorkshared;
        public string? CentralModelPath => _inner.CentralModelPath;

        public MCPBridge.RevitAdapter.ITransactionAdapter CreateTransaction(string name) => _riggedTransaction;

        public FakeTransactionGroupAdapter? LastTransactionGroup { get; private set; }

        public MCPBridge.RevitAdapter.ITransactionGroupAdapter CreateTransactionGroup(string name)
        {
            LastTransactionGroup = new FakeTransactionGroupAdapter(name);
            return LastTransactionGroup;
        }
    }
}
