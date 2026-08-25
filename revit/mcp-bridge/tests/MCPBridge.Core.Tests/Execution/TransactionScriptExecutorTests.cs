using System.Threading;
using System.Threading.Tasks;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Tests.Fakes;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

public class TransactionScriptExecutorTests
{
    private static TransactionScriptExecutor NewExecutor() => new(new RoslynScriptRunner());

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

        public MCPBridge.RevitAdapter.ITransactionAdapter CreateTransaction(string name) => _riggedTransaction;

        public FakeTransactionGroupAdapter? LastTransactionGroup { get; private set; }

        public MCPBridge.RevitAdapter.ITransactionGroupAdapter CreateTransactionGroup(string name)
        {
            LastTransactionGroup = new FakeTransactionGroupAdapter(name);
            return LastTransactionGroup;
        }
    }
}
