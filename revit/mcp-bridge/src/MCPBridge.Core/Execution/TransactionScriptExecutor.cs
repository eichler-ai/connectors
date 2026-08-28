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
/// PRD §07 (phase 02): the transaction's Failures API results (warnings auto-dismissed, any error
/// forces a rollback) are read from ITransactionAdapter.CommitFailures once, after Commit() returns.
/// Dialogs seen via DialogBoxShowing during the run (ActiveDialogContext) and those failures are both
/// folded into the same notices[] list, so a script's result always shows everything that was
/// auto-resolved on its behalf in one place -- including on a cancelled run, since a dialog may well be
/// what the script was stuck behind when it got cancelled.
///
/// PRD §09: files published via ScriptGlobals.Publish are a sibling list, files[] -- read directly off
/// the ScriptGlobals instance this method itself constructs, once the run finishes. Unlike dialog
/// overrides (ActiveDialogContext), Publish's state doesn't need a static bridge to reach here: this
/// method already holds the one ScriptGlobals instance for this run, so it can just read it back --
/// no other component (an OnStartup-registered handler with no reference to this run, the way
/// DialogBoxShowing's handler has none) needs to reach into it from outside.
///
/// PRD §14: this class is also what makes confirm_lifecycle_actions meaningful. The rollback described
/// above is precisely the boundary the confirmation gate is drawn around -- it covers document CONTENT,
/// so a script that throws undoes its work automatically, and the gated members (Close/Save/SaveAs/
/// SynchronizeWithCentral/Print/RelinquishOwnership) are gated because they act outside it and nothing
/// here can undo them. The flag is just forwarded to RoslynScriptRunner, which decides per run.
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
        CancellationToken cancellationToken,
        string? exportsDirectoryPath = null,
        string? importsDirectoryPath = null,
        bool overwriteOutputFiles = false,
        bool confirmLifecycleActions = false)
    {
        var group = document.CreateTransactionGroup(TransactionName);
        var transaction = document.CreateTransaction(TransactionName);

        group.Start();
        transaction.Start();

        var globals = new ScriptGlobals(
            document, uiApplication, uiDocument, cancellationToken,
            exportsDirectoryPath, importsDirectoryPath, overwriteOutputFiles);
        ActiveDialogContext.SetActive(globals.DialogResultOverrides);

        try
        {
            var outcome = await _runner
                .RunAsync(scriptText, globals, cancellationToken, confirmLifecycleActions)
                .ConfigureAwait(false);

            if (!outcome.Success)
            {
                RollBackBoth(transaction, group);
                // Commit() never ran -- no failures-API notices to fold in, but a dialog may still have
                // fired mid-script before it failed or was cancelled (PRD §07: this is precisely the
                // headline case -- a script stuck behind a dialog gets auto-cancelled by max_duration_ms).
                // Same reasoning applies to files[] (PRD §09): a script may have published a file before
                // it threw/was cancelled, and that publication must still be reported here.
                var dialogNotices = ActiveDialogContext.DrainRecorded();
                var publishedFiles = globals.PublishedFiles;
                if (dialogNotices.Count == 0 && publishedFiles.Count == 0)
                {
                    return outcome;
                }

                return outcome.WasCancelled
                    ? ScriptExecutionOutcome.Cancelled(outcome.StdOut, dialogNotices, publishedFiles)
                    : ScriptExecutionOutcome.Failed(outcome.Exception!, outcome.StdOut, dialogNotices, publishedFiles);
            }

            TransactionCommitResult commitResult;
            try
            {
                commitResult = transaction.Commit();
            }
            catch (Exception ex)
            {
                RollBackBoth(transaction, group);
                var failedNotices = CombinedNotices(transaction.CommitFailures);
                return ScriptExecutionOutcome.Failed(ex, outcome.StdOut, failedNotices, globals.PublishedFiles);
            }

            var commitFailures = transaction.CommitFailures;

            if (commitResult == TransactionCommitResult.RolledBack)
            {
                // Revit already rolled back the Transaction itself (ProceedWithRollBack) -- only the
                // TransactionGroup still needs an explicit rollback; calling transaction.RollBack()
                // again here would be invalid.
                group.RollBack();
                var errorMessage = commitFailures.LastOrDefault(f => f.IsError)?.Message
                    ?? "A transaction failure forced a rollback.";
                var rolledBackNotices = CombinedNotices(commitFailures);
                return ScriptExecutionOutcome.Failed(new InvalidOperationException(errorMessage), outcome.StdOut, rolledBackNotices, globals.PublishedFiles);
            }

            group.Assimilate();
            var completedNotices = CombinedNotices(commitFailures);
            return ScriptExecutionOutcome.Completed(outcome.ReturnValue, outcome.StdOut, completedNotices, globals.PublishedFiles);
        }
        finally
        {
            ActiveDialogContext.ClearActive();
        }
    }

    private static List<DiagnosticRecord> CombinedNotices(IReadOnlyList<FailureSummary> commitFailures)
    {
        var failureNotices = commitFailures.Select(ToDiagnosticRecord).ToList();
        var dialogNotices = ActiveDialogContext.DrainRecorded();
        if (dialogNotices.Count > 0)
        {
            failureNotices.AddRange(dialogNotices);
        }

        return failureNotices;
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
        // Review finding: an unguarded transaction.RollBack() here could itself throw (e.g. the
        // Transaction was already closed by Revit's own Failures API resolution before Commit() threw),
        // which previously propagated uncaught and replaced the real failure being reported with a
        // rollback-time exception instead. Each rollback is now independently best-effort.
        SafeRollBack(transaction.RollBack);
        SafeRollBack(group.RollBack);
    }

    private static void SafeRollBack(Action rollBack)
    {
        try
        {
            rollBack();
        }
        catch
        {
            // Best-effort: never let a rollback-time exception mask the original failure being reported.
        }
    }
}
