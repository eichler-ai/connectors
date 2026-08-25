using System;
using System.Threading;
using System.Threading.Tasks;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Wraps one script run in a Transaction/TransactionGroup (PRD §06 step 4): commit +
/// assimilate on success, roll back both on any failure (thrown exception, compile
/// error, or cooperative cancellation) so a failed script never leaves partial
/// document changes behind. Deliberately simple for phase 01 -- no
/// IFailuresPreprocessor hookup here, that's phase 02 (PRD §15).
/// </summary>
public sealed class TransactionScriptExecutor
{
    private const string TransactionName = "MCP Bridge Script";

    private readonly RoslynScriptRunner _runner;

    public TransactionScriptExecutor(RoslynScriptRunner runner)
    {
        _runner = runner;
    }

    public async Task<ScriptExecutionOutcome> ExecuteAsync(
        IDocumentAdapter document,
        IUiApplicationAdapter uiApplication,
        IUiDocumentAdapter? uiDocument,
        string scriptText,
        CancellationToken cancellationToken)
    {
        var group = document.CreateTransactionGroup(TransactionName);
        var transaction = document.CreateTransaction(TransactionName);

        group.Start();
        transaction.Start();

        var globals = new ScriptGlobals(document, uiApplication, uiDocument, cancellationToken);
        var outcome = await _runner.RunAsync(scriptText, globals, cancellationToken).ConfigureAwait(false);

        if (!outcome.Success)
        {
            RollBackBoth(transaction, group);
            return outcome;
        }

        try
        {
            transaction.Commit();
            group.Assimilate();
            return outcome;
        }
        catch (Exception ex)
        {
            RollBackBoth(transaction, group);
            return ScriptExecutionOutcome.Failed(ex, outcome.StdOut);
        }
    }

    private static void RollBackBoth(ITransactionAdapter transaction, ITransactionGroupAdapter group)
    {
        transaction.RollBack();
        group.RollBack();
    }
}
