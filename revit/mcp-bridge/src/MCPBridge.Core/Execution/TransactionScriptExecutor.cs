using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MCPBridge.Core.Diagnostics;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Wraps one script run in a Transaction/TransactionGroup (PRD §06 step 4): commit + assimilate on
/// success, roll back both on any failure (thrown exception, compile error, or cooperative
/// cancellation) so a failed script never leaves partial document changes behind.
///
/// PRD §07 (phase 02): an IFailuresPreprocessor is registered on the transaction before Commit() --
/// warnings are auto-dismissed, any error rolls back and surfaces as a script failure. Dialogs seen via
/// DialogBoxShowing during the run (ActiveDialogContext) and transaction failures seen via the Failures
/// API are both folded into the same notices[] list, so a script's result always shows everything that
/// was auto-resolved on its behalf in one place.
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

        var failureNotices = new List<DiagnosticRecord>();
        transaction.SetFailuresObserver(summaries => failureNotices.AddRange(summaries.Select(ToDiagnosticRecord)));

        var globals = new ScriptGlobals(document, uiApplication, uiDocument, cancellationToken);
        ActiveDialogContext.SetActive(globals.DialogResultOverrides);
        try
        {
            var outcome = await _runner.RunAsync(scriptText, globals, cancellationToken).ConfigureAwait(false);

            if (!outcome.Success)
            {
                RollBackBoth(transaction, group);
                // Commit() never ran -- no failures-API notices to fold in, but a dialog may still have
                // fired mid-script before it failed, so still drain ActiveDialogContext.
                var dialogNotices = ActiveDialogContext.DrainRecorded();
                return dialogNotices.Count == 0 ? outcome : outcome switch
                {
                    { WasCancelled: true } => outcome,
                    _ => ScriptExecutionOutcome.Failed(outcome.Exception!, outcome.StdOut, dialogNotices),
                };
            }

            TransactionCommitResult commitResult;
            try
            {
                commitResult = transaction.Commit();
            }
            catch (Exception ex)
            {
                RollBackBoth(transaction, group);
                return ScriptExecutionOutcome.Failed(ex, outcome.StdOut, CombinedNotices(failureNotices));
            }

            if (commitResult == TransactionCommitResult.RolledBack)
            {
                // Revit already rolled back the Transaction itself (ProceedWithRollBack) -- only the
                // TransactionGroup still needs an explicit rollback; calling transaction.RollBack()
                // again here would be invalid.
                group.RollBack();
                var errorMessage = failureNotices.LastOrDefault(n => n.Severity == DiagnosticSeverity.Error)?.Message
                    ?? "A transaction failure forced a rollback.";
                return ScriptExecutionOutcome.Failed(new InvalidOperationException(errorMessage), outcome.StdOut, CombinedNotices(failureNotices));
            }

            group.Assimilate();
            return ScriptExecutionOutcome.Completed(outcome.ReturnValue, outcome.StdOut, CombinedNotices(failureNotices));
        }
        finally
        {
            ActiveDialogContext.ClearActive();
        }
    }

    private static IReadOnlyList<DiagnosticRecord> CombinedNotices(List<DiagnosticRecord> failureNotices)
    {
        var dialogNotices = ActiveDialogContext.DrainRecorded();
        if (dialogNotices.Count == 0)
        {
            return failureNotices;
        }

        var combined = new List<DiagnosticRecord>(failureNotices);
        combined.AddRange(dialogNotices);
        return combined;
    }

    private static DiagnosticRecord ToDiagnosticRecord(FailureSummary failure) => DiagnosticRecord.Create(
        failure.IsError ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
        failure.IsError ? "transaction-failure-error" : "transaction-failure-warning",
        DiagnosticSource.Dialogs,
        failure.Message,
        detail: new Dictionary<string, object?>
        {
            ["failure_definition_id"] = failure.FailureDefinitionId,
            ["failing_element_ids"] = failure.FailingElementIds,
        },
        remedy: null);

    private static void RollBackBoth(ITransactionAdapter transaction, ITransactionGroupAdapter group)
    {
        transaction.RollBack();
        group.RollBack();
    }
}
