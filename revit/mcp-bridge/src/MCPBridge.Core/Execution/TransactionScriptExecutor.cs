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
/// ISSUE #24: that is now true of EVERY document the run touches, not just the active one. The pair
/// above is opened for the ambient document before the script runs; a document the script creates via
/// ScriptGlobals.CreateProjectDocument/CreateFamilyDocument gets its own pair, opened lazily at the
/// moment of creation. All of them live in one <see cref="ManagedDocumentTransactions"/>, which owns
/// commit ordering and partial-failure semantics -- see that class for why the ambient document is
/// committed LAST, and PartialCommitNotice below for what happens when a later commit fails after an
/// earlier one already succeeded. The script never commits or rolls back anything itself, with N
/// documents exactly as with one; its own return-or-throw governs all of them uniformly.
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
internal sealed class TransactionScriptExecutor
{
    private const string TransactionName = "MCP Bridge Script";

    private readonly RoslynScriptRunner _runner;

    /// <summary>See <see cref="RoslynScriptRunner.WarmupCompile"/> -- exposed here so the AddIn's
    /// startup wiring can warm the pipeline without reaching the runner (which stays fully owned by
    /// this executor).</summary>
    internal void WarmupCompile() => _runner.WarmupCompile();

    /// <summary>#67: see <see cref="RoslynScriptRunner.TryPreflight"/>. Exposed here (like WarmupCompile)
    /// so the dispatcher, which holds only this executor, can compile + denylist-check a script on the
    /// connection thread before raising the ExternalEvent and reject an invalid one immediately. No
    /// transaction is opened and no Revit object is touched -- this is a pure compile-time check.</summary>
    internal ScriptExecutionOutcome? TryPreflight(string scriptText, bool confirmLifecycleActions = false)
        => _runner.TryPreflight(scriptText, confirmLifecycleActions);

    /// <summary>#67: see <see cref="RoslynScriptRunner.IsWarm"/>. The dispatcher gates its pre-flight on
    /// this so a cold compile never lands on the response path.</summary>
    internal bool IsWarm => _runner.IsWarm;

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
        // Issue #24: N documents, not one. The ambient (active) document is opened here, before the
        // script runs, exactly as before; any document the script goes on to create through
        // ScriptGlobals.CreateProjectDocument/CreateFamilyDocument is opened lazily into this same set
        // as it is created. Commit/rollback/notices then all loop over every document.
        var transactions = new ManagedDocumentTransactions(TransactionName, uiApplication);
        transactions.Open(document, isAmbient: true);

        var globals = new ScriptGlobals(
            document, uiApplication, uiDocument, cancellationToken,
            exportsDirectoryPath, importsDirectoryPath, overwriteOutputFiles, transactions);
        ActiveDialogContext.SetActive(globals.DialogResultOverrides);

        try
        {
            var outcome = await _runner
                .RunAsync(scriptText, globals, cancellationToken, confirmLifecycleActions)
                .ConfigureAwait(false);

            if (!outcome.Success)
            {
                transactions.RollBackAll();
                // Commit() never ran -- no failures-API notices to fold in, but a dialog may still have
                // fired mid-script before it failed or was cancelled (PRD §07: this is precisely the
                // headline case -- a script stuck behind a dialog gets auto-cancelled by max_duration_ms).
                // Same reasoning applies to files[] (PRD §09): a script may have published a file before
                // it threw/was cancelled, and that publication must still be reported here.
                var dialogNotices = new List<DiagnosticRecord>(ActiveDialogContext.DrainRecorded());
                var publishedFiles = globals.PublishedFiles;
                // A settle on a FAILED run is the case that matters most (issue #132): the rollback
                // below cannot undo it, so "the script failed" would otherwise imply nothing survived
                // when something permanently did.
                dialogNotices.AddRange(transactions.SettledFailures.Select(ToDiagnosticRecord));
                dialogNotices.AddRange(transactions.Settlements.Select(SettleNotice));
                // #122: documents this run CREATED outlive the failure -- the rollback above undid their
                // content, not their existence, so they stay open in the session. This is the error path the
                // #114 leak came from: without a handle, an agent that threw mid-script cannot match them by
                // Title (it never saw one). Report them so it can.
                var createdOnFailure = CreatedDocumentsNotice(transactions.CreatedDocuments);
                if (createdOnFailure is not null)
                {
                    dialogNotices.Add(createdOnFailure);
                }

                if (dialogNotices.Count == 0 && publishedFiles.Count == 0)
                {
                    return outcome;
                }

                return outcome.WasCancelled
                    ? ScriptExecutionOutcome.Cancelled(outcome.StdOut, dialogNotices, publishedFiles)
                    : ScriptExecutionOutcome.Failed(outcome.Exception!, outcome.StdOut, dialogNotices, publishedFiles);
            }

            // The script's own code has already finished at this point -- with one document or with N,
            // every commit happens here, in the executor, never in the script.
            // Read before CommitAll purely for readability. An earlier comment here claimed CommitAll
            // clears the settlement log along with the entry set -- it does not; nothing clears
            // _settlements at all -- so the ordering is not load-bearing, and the claim would have sent a
            // future reader chasing a hazard that does not exist.
            var settlements = transactions.Settlements;
            var commit = transactions.CommitAll();
            var notices = CombinedNotices(commit.CommitFailures);
            notices.AddRange(settlements.Select(SettleNotice));
            // #122: report created documents on the success path too -- they remain open and unsaved, and a
            // script that created them on purpose still needs their handle to close or save them next.
            var createdOnSuccess = CreatedDocumentsNotice(transactions.CreatedDocuments);
            if (createdOnSuccess is not null)
            {
                notices.Add(createdOnSuccess);
            }

            if (!commit.Success)
            {
                if (commit.IsPartial)
                {
                    notices.Add(PartialCommitNotice(commit));
                }

                return ScriptExecutionOutcome.Failed(commit.Failure!, outcome.StdOut, notices, globals.PublishedFiles);
            }

            return ScriptExecutionOutcome.Completed(outcome.ReturnValue, outcome.StdOut, notices, globals.PublishedFiles);
        }
        finally
        {
            // Safety net, not the normal path: every branch above has already committed or rolled back,
            // and ManagedDocumentTransactions drops its entries when it does, so this is a no-op then.
            // It matters when the runner throws instead of returning a failed outcome -- without it,
            // every managed document's Transaction and TransactionGroup would be left open in the live
            // Revit session with nothing holding a reference to them.
            transactions.RollBackAll();
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

    /// <summary>
    /// One notice per Connector.Settle (issue #132, decision 2). Settle is the ONLY scope that notices,
    /// and the reason is signal rather than literalism: it is the irreversible one -- it makes this
    /// document's changes permanent or discards them, immediately -- while WithTransaction and
    /// WithoutTransaction leave the group's rollback boundary intact and have nothing to confess. A
    /// notice per scope would bury these under any script that writes in a loop.
    ///
    /// Severity is Warning rather than Info deliberately: settling the AMBIENT document gives up the
    /// roll-back-on-throw guarantee for a real model a person has open, which is the single largest
    /// change to what a failed run means, and PRD §01 does not permit it passing silently.
    /// </summary>
    internal static DiagnosticRecord SettleNotice(ManagedDocumentTransactions.SettlementRecord settlement) =>
        DiagnosticRecord.Create(
            DiagnosticSeverity.Warning,
            settlement.Kept ? "document-settled-kept" : "document-settled-discarded",
            DiagnosticSource.Execution,
            settlement.Kept
                ? $"'{settlement.Document}' was settled with keep: true, so every change made to it before that " +
                  "point is now permanent -- a later failure in this script can no longer undo them."
                : $"'{settlement.Document}' was settled with keep: false, so every change made to it before that " +
                  "point was discarded.",
            detail: new Dictionary<string, object?>
            {
                ["document"] = settlement.Document,
                ["kept"] = settlement.Kept,
            },
            remedy: settlement.Kept
                ? new[] { "If this was not intended, the changes cannot be rolled back -- inspect the document and correct it explicitly." }
                : null);

    /// <summary>
    /// #122: reports the documents THIS run created, on EVERY run (success and failure), so a created
    /// document that outlives its run never does so silently (PRD §01 observability-over-silence). Null --
    /// no notice -- when nothing was created, which is the common case. No new <c>Connector</c> member and
    /// no mid-run transaction-release: the identities are already tracked by ManagedDocumentTransactions,
    /// and this only surfaces them. Info severity: it is a handle, not a fault -- the document is intact,
    /// just still open.
    /// </summary>
    internal static DiagnosticRecord? CreatedDocumentsNotice(IReadOnlyList<ManagedDocumentTransactions.CreatedDocumentRecord> created)
    {
        if (created.Count == 0)
        {
            return null;
        }

        var named = string.Join(", ", created.Select(d => $"'{d.Title}' ({d.DocumentId})"));
        return DiagnosticRecord.Create(
            DiagnosticSeverity.Info,
            "script-created-documents",
            DiagnosticSource.Execution,
            $"This run created {created.Count} document(s) that remain open and unsaved in the Revit session: {named}. " +
            "They outlive this run -- if the script failed, its rollback undid their contents but not their existence -- " +
            "so they are yours to close or save; match them by the document_id (or Title) in detail.created_documents.",
            detail: new Dictionary<string, object?>
            {
                ["created_documents"] = created
                    .Select(d => new Dictionary<string, object?>
                    {
                        ["title"] = d.Title,
                        ["document_id"] = d.DocumentId,
                    })
                    .ToArray(),
            },
            remedy: new[]
            {
                "To close or save one, target it by its document_id in a follow-up execute_script call with " +
                "confirm_lifecycle_actions: true (e.g. Document.Close(false), or SaveAs to keep it). Leaving it open is fine too.",
            });
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

    /// <summary>
    /// Issue #24: with N documents a commit failure can be genuinely PARTIAL -- an earlier document's
    /// transaction already committed, and Revit offers no way to un-commit one. PRD §01's
    /// observability-over-silence principle makes saying so non-optional: the run is reported as failed
    /// (it is), and this notice states exactly which documents kept their changes and which did not,
    /// rather than letting "failed" imply nothing happened anywhere.
    ///
    /// Emitted when something actually committed, and ALSO when a document's own rollback threw: an
    /// indeterminate document is the case an agent most needs told about, so it cannot be the case that
    /// gets no notice. A failure on the FIRST document that then rolls everything back cleanly commits
    /// nothing and leaves nothing unknown, so it needs no such notice and gets none.
    /// </summary>
    private static DiagnosticRecord PartialCommitNotice(ManagedDocumentCommitResult commit)
    {
        // The "so those changes remain" clause is conditional on something ACTUALLY having committed
        // (second-round review finding). This notice is also emitted when the FIRST document fails and
        // its own rollback throws -- CommittedDocuments is empty there, and the unconditional wording
        // read "...after 0 other document(s) had already committed ... so those changes remain", which
        // claims surviving changes that do not exist, in the one notice whose entire job is being honest
        // about partial state.
        var message = commit.CommittedDocuments.Count > 0
            ? $"The script ran to completion but one document failed to commit after {commit.CommittedDocuments.Count} " +
              "other document(s) had already committed; a committed Revit transaction cannot be un-committed, so " +
              $"those changes remain. Committed: {Describe(commit.CommittedDocuments)}. " +
              $"Rolled back: {Describe(commit.RolledBackDocuments)}."
            : "The script ran to completion but a document failed to commit before any document had committed, " +
              $"so no changes were kept. Rolled back: {Describe(commit.RolledBackDocuments)}.";

        // Both of these are about inspecting what COMMITTED, so they are only offered when something
        // did -- see the message's own conditional above for why the zero-committed case exists at all.
        var remedy = new List<string>();
        if (commit.CommittedDocuments.Count > 0)
        {
            // Independent PR review finding (PR #28 #1): this used to unconditionally claim every
            // committed document is "unsaved and in-memory." That was true when CreateProjectDocument/
            // CreateFamilyDocument were the only two members of this tier, but ScriptGlobals.OpenForWriting
            // adds a genuine adopt-by-title WRITE path (a script can now open a managed transaction on a
            // document it did not itself create this run, including one that is saved on disk), so the
            // claim is a straightforward lie whenever ManagedDocumentCommitResult.AnyCommittedDocumentMayBeReal
            // is true. Report honestly instead of guessing which committed document was which.
            remedy.Add(
                commit.AnyCommittedDocumentMayBeReal
                    ? "At least one committed document may be a real, saved document -- one adopted via " +
                      "OpenForWriting, or the ambient document itself -- not necessarily an unsaved, " +
                      "in-memory one created this run. Do not assume the committed changes are throwaway."
                    : "Documents a script creates are unsaved and in-memory, so nothing was written to disk -- " +
                      "the committed changes exist only in this Revit session.");
            // NOT "or undo": the connector has no way to un-commit an already-committed Transaction (Revit
            // itself offers none), and this run's own script can never open a fresh transaction on a
            // document from a later, separate execute_script call (ScriptApiDenylist check 1) -- so even
            // with OpenForWriting's adopt-by-title path, a follow-up script can inspect a committed
            // document and open ITS OWN new managed transaction on it (via OpenForWriting again) to make
            // further changes, but it can never undo what already committed.
            remedy.Add(
                "Find a committed document by Title in UIApplication.Application.Documents from a follow-up " +
                "script to inspect what landed.");
        }

        // Named separately and last, never folded into "Rolled back" (independent PR review finding).
        // Claiming a document rolled back when its rollback itself threw is the one lie this notice must
        // not tell, and omitting it entirely would be the silence PRD §01 forbids -- an unknown-state
        // document is exactly what a human or agent has to go and look at.
        if (commit.UnknownStateDocuments.Count > 0)
        {
            message +=
                $" Rollback FAILED, state unknown: {Describe(commit.UnknownStateDocuments)} -- the rollback " +
                "for these threw, so this connector cannot say whether their changes were undone.";
            remedy.Add(
                "Inspect the unknown-state document(s) directly in Revit before relying on them; their " +
                "contents were neither confirmed committed nor confirmed rolled back.");
        }

        return DiagnosticRecord.Create(
            DiagnosticSeverity.Error,
            "script-partial-commit",
            DiagnosticSource.Execution,
            message,
            detail: new Dictionary<string, object?>
            {
                ["committed_documents"] = commit.CommittedDocuments,
                ["rolled_back_documents"] = commit.RolledBackDocuments,
                ["unknown_state_documents"] = commit.UnknownStateDocuments,
            },
            remedy: remedy);
    }

    /// <summary>"(none)" rather than an empty string, so a reader can tell an empty list from a bug.</summary>
    private static string Describe(IReadOnlyList<string> documents) =>
        documents.Count == 0 ? "(none)" : string.Join(", ", documents);
}
