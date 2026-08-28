using System;
using System.Collections.Generic;
using System.Linq;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Every document one script run may write to, each with the Transaction/TransactionGroup pair this
/// connector opened and owns for it (issue #24).
///
/// WHY N PAIRS AND NOT ONE. TransactionScriptExecutor used to manage exactly one pair, around the
/// ambient (active) document. Revit's one-open-transaction rule is per-DOCUMENT, not global, so a
/// document a script creates mid-run is simply not covered by that pair -- writing to it threw
/// ModificationOutsideTransactionException, and the script could not open its own Transaction either,
/// because ScriptApiDenylist check 1 refuses that unconditionally. Rather than teaching the denylist
/// which document a Transaction targets (assessed and rejected: Revit hands back different wrapper
/// objects for "the same" document depending on the API entry point, and DocumentIdentity is weakest
/// for exactly the unsaved documents this is about), the connector opens the transaction itself, in
/// the same step that creates the document. The denylist rule stays completely unconditional and gains
/// no new bypass surface; if this plumbing has a bug the failure mode is a broken FEATURE, not a
/// broken security boundary.
///
/// A SCRIPT NEVER COMMITS OR ROLLS BACK ANYTHING, with N documents exactly as with one. Every commit
/// happens here, after the script's C# has already finished running, and the script's own
/// return-or-throw governs all N uniformly: it returned normally, so commit everything; it threw or
/// was cancelled, so roll everything back. There is no per-document success concept for a script to
/// express, and none is invented.
///
/// COMMIT ORDER IS DELIBERATE: created documents first, in creation order, and the AMBIENT document
/// last. A committed Revit transaction cannot be un-committed, so ordering is the only lever over what
/// a partial failure can damage -- and committing the ambient document last means any failure among
/// the created ones is still answerable by rolling the ambient one back. The ambient document is a
/// real model a human has open; a created one is unsaved and in-memory, touching no file, no central
/// model and no session, so confining partial-commit fallout to those is strictly the better trade.
/// See <see cref="ManagedDocumentCommitResult"/> for how what did land is reported (PRD §01).
/// </summary>
public sealed class ManagedDocumentTransactions
{
    private sealed class Entry
    {
        public Entry(IDocumentAdapter document, ITransactionGroupAdapter group, ITransactionAdapter transaction, bool isAmbient)
        {
            Document = document;
            Group = group;
            Transaction = transaction;
            IsAmbient = isAmbient;
        }

        public IDocumentAdapter Document { get; }
        public ITransactionGroupAdapter Group { get; }
        public ITransactionAdapter Transaction { get; }
        public bool IsAmbient { get; }

        /// <summary>
        /// How this document is named in a partial-commit report. Title, because that is also how a
        /// created document is addressed across execute_script calls (PRD §14: it stays in
        /// Application.Documents and is found there by Title -- there is no document_id for it), so the
        /// name in the report is one an agent can act on.
        /// </summary>
        public string Describe() => IsAmbient ? $"{Document.Title} (active document)" : Document.Title;
    }

    private readonly string _transactionName;
    private readonly IUiApplicationAdapter _uiApplication;
    private readonly List<Entry> _entries = new();

    public ManagedDocumentTransactions(string transactionName, IUiApplicationAdapter uiApplication)
    {
        _transactionName = transactionName;
        _uiApplication = uiApplication;
    }

    /// <summary>How many documents are currently under management, ambient included.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Opens and starts a TransactionGroup + Transaction for <paramref name="document"/> and tracks
    /// them. <paramref name="isAmbient"/> marks the run's active document -- at most one, opened by
    /// TransactionScriptExecutor before the script runs; created documents are opened lazily as the
    /// script creates them.
    /// </summary>
    public void Open(IDocumentAdapter document, bool isAmbient = false)
    {
        var group = document.CreateTransactionGroup(_transactionName);
        group.Start();

        ITransactionAdapter transaction;
        try
        {
            transaction = document.CreateTransaction(_transactionName);
            transaction.Start();
        }
        catch
        {
            // The group started but the transaction did not, so nothing tracks the group and nothing
            // would ever close it. Undo it here rather than leaking an open group into the session.
            SafeRollBack(group.RollBack);
            throw;
        }

        _entries.Add(new Entry(document, group, transaction, isAmbient));
    }

    /// <summary>
    /// Creates a new project document and opens its managed transaction in one step -- the whole point
    /// of issue #24's chosen approach. See <see cref="IDocumentCreationSource.CreateProjectDocument"/>
    /// for template resolution.
    /// </summary>
    public IDocumentAdapter CreateAndOpenProjectDocument(string? templatePath) =>
        CreateAndOpen(source => source.CreateProjectDocument(templatePath));

    /// <summary>Family-document counterpart of <see cref="CreateAndOpenProjectDocument"/>.</summary>
    public IDocumentAdapter CreateAndOpenFamilyDocument(string templatePath) =>
        CreateAndOpen(source => source.CreateFamilyDocument(templatePath));

    private IDocumentAdapter CreateAndOpen(Func<IDocumentCreationSource, IDocumentAdapter> create)
    {
        var source = _uiApplication as IDocumentCreationSource
            ?? throw new NotSupportedException(
                $"creating a document needs a live Revit session, but {_uiApplication.GetType().Name} does not " +
                $"implement {nameof(IDocumentCreationSource)}. Only the live adapter does -- " +
                "Autodesk.Revit.ApplicationServices.Application is non-constructible outside a running " +
                "Revit session, so a fake genuinely cannot create a document. A test that needs to create " +
                "one belongs in the tier-2 live harness (revit/test-harness), not MCPBridge.Core.Tests.");

        var created = create(source);
        Open(created);
        return created;
    }

    /// <summary>
    /// Rolls back every managed document, most recently opened first, best-effort. Used for every
    /// failed run -- a thrown script, a compile/denylist rejection, or a cancellation -- so a failure
    /// leaves no partial changes behind in ANY document, not just the ambient one.
    ///
    /// IDEMPOTENT BY DESIGN -- entries are dropped once closed, here and in <see cref="CommitAll"/>, so
    /// calling this a second time does nothing. That is what lets TransactionScriptExecutor call it
    /// unconditionally from its `finally` as a safety net: if the runner itself throws rather than
    /// returning a failed outcome, no branch has rolled anything back and every document's transaction
    /// would otherwise be left open in the live session. Self-review finding -- the single-document
    /// version had the same hole and it simply mattered less.
    /// </summary>
    public void RollBackAll()
    {
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            SafeRollBack(_entries[i].Transaction.RollBack);
            SafeRollBack(_entries[i].Group.RollBack);
        }

        _entries.Clear();
    }

    /// <summary>
    /// Commits every managed document in the order described on this class, stopping at the first
    /// failure and rolling back everything that has not already committed. Never throws: a commit-time
    /// exception is returned in the result so the caller can report it alongside what did land.
    ///
    /// Like <see cref="RollBackAll"/>, this leaves the set empty: every document has been closed one way
    /// or another, so the executor's `finally` net has nothing left to do.
    /// </summary>
    public ManagedDocumentCommitResult CommitAll()
    {
        var order = _entries.Where(e => !e.IsAmbient).Concat(_entries.Where(e => e.IsAmbient)).ToList();

        // Every entry is closed by the time this returns, one way or another -- drop them so a later
        // RollBackAll (the executor's `finally` safety net) is a no-op rather than a double close.
        _entries.Clear();

        var failures = new List<FailureSummary>();
        var committed = new List<string>();
        var rolledBack = new List<string>();
        Exception? failure = null;

        var index = 0;
        for (; index < order.Count; index++)
        {
            var entry = order[index];
            if (TryCommit(entry, failures, out var error))
            {
                committed.Add(entry.Describe());
                continue;
            }

            rolledBack.Add(entry.Describe());
            failure = error;
            index++;
            break;
        }

        if (failure is null)
        {
            return ManagedDocumentCommitResult.Succeeded(failures, committed);
        }

        // Whatever has not been attempted yet must not be left open: the run is a failure now, and
        // these documents have committed nothing, so rolling them back is both possible and correct.
        for (; index < order.Count; index++)
        {
            SafeRollBack(order[index].Transaction.RollBack);
            SafeRollBack(order[index].Group.RollBack);
            rolledBack.Add(order[index].Describe());
        }

        return ManagedDocumentCommitResult.Failed(failure, failures, committed, rolledBack);
    }

    /// <summary>
    /// Commits one document's pair, mirroring exactly what the single-document version of this class
    /// did: Commit(), then Assimilate() on the group. Failure has three distinct shapes and they need
    /// different unwinding, which is why this is not a one-liner.
    /// </summary>
    private static bool TryCommit(Entry entry, List<FailureSummary> failures, out Exception? error)
    {
        TransactionCommitResult result;
        try
        {
            result = entry.Transaction.Commit();
        }
        catch (Exception ex)
        {
            failures.AddRange(entry.Transaction.CommitFailures);
            SafeRollBack(entry.Transaction.RollBack);
            SafeRollBack(entry.Group.RollBack);
            error = ex;
            return false;
        }

        failures.AddRange(entry.Transaction.CommitFailures);

        if (result == TransactionCommitResult.RolledBack)
        {
            // Revit already rolled back the Transaction itself (ProceedWithRollBack) -- only the
            // TransactionGroup still needs an explicit rollback; calling Transaction.RollBack() again
            // here would be invalid.
            SafeRollBack(entry.Group.RollBack);
            error = new InvalidOperationException(
                entry.Transaction.CommitFailures.LastOrDefault(f => f.IsError)?.Message
                ?? "A transaction failure forced a rollback.");
            return false;
        }

        try
        {
            entry.Group.Assimilate();
        }
        catch (Exception ex)
        {
            // The Transaction is already committed and Revit offers no way to un-commit it; rolling
            // the GROUP back is the only remaining lever, and it is best-effort like every other
            // rollback here. Reported as a failure either way, never silently swallowed.
            SafeRollBack(entry.Group.RollBack);
            error = ex;
            return false;
        }

        error = null;
        return true;
    }

    private static void SafeRollBack(Action rollBack)
    {
        try
        {
            rollBack();
        }
        catch
        {
            // Best-effort: never let a rollback-time exception mask the original failure being
            // reported, and never let one document's rollback stop another document's.
        }
    }
}
