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
///
/// INTERNAL, AND THAT IS A SECURITY BOUNDARY, NOT A STYLE CHOICE. RoslynScriptRunner.LoadableReferences()
/// references every assembly loaded in the Revit AppDomain -- MCPBridge.Core included -- so any PUBLIC
/// type here is a type an untrusted agent script can name and construct. Live-verified against real Revit
/// 2027 while this class was public: a script constructed `new MCPBridge.RevitAdapter.RevitDocumentAdapter(raw)`
/// on a document it had made itself, called CreateTransaction on it, wrote a Level and COMMITTED it --
/// a real, unmanaged transaction ScriptApiDenylist never saw, because the `new Transaction(...)` happens
/// inside adapter code rather than in the script's own syntax tree, which is all the AST walk examines.
/// This class was the same shape and strictly more powerful (Open/CreateAndOpen*/CommitAll/RollBackAll on
/// documents the executor owns). `internal` closes it structurally -- a script cannot name what it cannot
/// see -- which is why it was preferred over adding our own types to the denylist's tables: those are
/// meant to stay small and Revit-API-focused, and a list of forbidden names is only ever as complete as
/// the last person to remember to extend it. Reflection can still reach this, exactly as ScriptApiDenylist
/// documents for its own entries; that is the accepted GUARD-not-sandbox line, unchanged.
/// </summary>
internal sealed class ManagedDocumentTransactions
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
            // SafeRollBack's success flag is deliberately discarded here, unlike in CommitAll. This path
            // runs when the whole run already failed, so there is no partial-commit report to be honest
            // WITHIN: nothing committed anywhere, and the outcome the caller returns is already the
            // script's own exception. CommitAll is the case where the distinction changes what an agent
            // is told, and that is where it is tracked.
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
    /// Like <see cref="RollBackAll"/>, this leaves the set empty when it returns: every document has been
    /// closed one way or another, so the executor's `finally` net has nothing left to do. The one case it
    /// does NOT empty the set is an exception escaping it anyway -- see the clearing comment below, which
    /// is where that deliberate asymmetry lives.
    /// </summary>
    public ManagedDocumentCommitResult CommitAll()
    {
        var order = _entries.Where(e => !e.IsAmbient).Concat(_entries.Where(e => e.IsAmbient)).ToList();

        var result = CommitInOrder(order);

        // CLEARED HERE, AFTER the work, and DELIBERATELY NOT IN A `finally` (independent PR review
        // finding). Reaching this line means every document above was closed one way or another, so the
        // set must end up empty -- otherwise the executor's `finally` RollBackAll would roll back what
        // this method just committed.
        //
        // But if CommitInOrder THROWS, control never gets here and _entries stays populated ON PURPOSE.
        // The bug was clearing up front: an unexpected escape (an adapter's CommitFailures getter,
        // Document.Title inside Describe()) then left the executor's net with nothing to roll back, and
        // every not-yet-committed document leaked an open Transaction/TransactionGroup into the live
        // Revit session. A `finally` would NOT have fixed that -- it runs on the exception path too, and
        // would clear the entries just the same. Only skipping the clear on that path preserves them.
        // The reads that can actually throw are individually guarded below as well; this covers the ones
        // nobody thought of.
        _entries.Clear();
        return result;
    }

    /// <summary>
    /// <see cref="CommitAll"/>'s body, split out so the entry list is cleared only when this returns
    /// normally -- see the comment at that call site, which is the whole reason this is a separate
    /// method. Expected NOT to throw (every adapter call is guarded); the split exists precisely for the
    /// case where that expectation turns out to be wrong.
    ///
    /// SECOND-ROUND REVIEW FINDING: that "every adapter call is guarded" claim used to be FALSE here.
    /// The three list-building calls below went through <c>entry.Describe()</c> directly rather than
    /// <see cref="SafeDescribe"/> two methods away, and Describe() reads Document.Title -- a live Revit
    /// call that can throw. When it threw AFTER a successful commit it escaped
    /// TransactionScriptExecutor.ExecuteAsync (whose try has a finally but no catch) as a raw unhandled
    /// exception, destroying the very `script-partial-commit` notice that exists to report which
    /// documents committed. All three now route through SafeDescribe, which is what makes the
    /// "never throws" contract on <see cref="CommitAll"/> actually true rather than merely intended.
    /// </summary>
    private static ManagedDocumentCommitResult CommitInOrder(List<Entry> order)
    {
        var failures = new List<FailureSummary>();
        var committed = new List<string>();
        var rolledBack = new List<string>();
        var unknownState = new List<string>();
        Exception? failure = null;

        var index = 0;
        for (; index < order.Count; index++)
        {
            var entry = order[index];
            var attempt = AttemptCommit(entry, failures);
            if (attempt.Succeeded)
            {
                committed.Add(SafeDescribe(entry));
                continue;
            }

            (attempt.RollbackVerified ? rolledBack : unknownState).Add(SafeDescribe(entry));
            failure = attempt.Error;
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
            var entry = order[index];
            var transactionUnwound = SafeRollBack(entry.Transaction.RollBack);
            var groupUnwound = SafeRollBack(entry.Group.RollBack);
            (transactionUnwound && groupUnwound ? rolledBack : unknownState).Add(SafeDescribe(entry));
        }

        return ManagedDocumentCommitResult.Failed(failure, failures, committed, rolledBack, unknownState);
    }

    /// <summary>
    /// What committing one document actually did. <see cref="RollbackVerified"/> is meaningful only when
    /// <see cref="Error"/> is non-null: it reports whether the best-effort unwind that followed the
    /// failure was itself observed to succeed, or threw and left this document in a state the connector
    /// cannot describe. Carried out of <see cref="AttemptCommit"/> as a value rather than a second `out`
    /// parameter so it is impossible to report a rollback the code never confirmed happened.
    /// </summary>
    private readonly struct CommitAttempt
    {
        private CommitAttempt(Exception? error, bool rollbackVerified)
        {
            Error = error;
            RollbackVerified = rollbackVerified;
        }

        public Exception? Error { get; }

        public bool RollbackVerified { get; }

        public bool Succeeded => Error is null;

        public static CommitAttempt Committed() => new(null, rollbackVerified: true);

        public static CommitAttempt Failed(Exception error, bool rollbackVerified) => new(error, rollbackVerified);
    }

    /// <summary>
    /// Commits one document's pair, mirroring exactly what the single-document version of this class
    /// did: Commit(), then Assimilate() on the group. Failure has three distinct shapes and they need
    /// different unwinding, which is why this is not a one-liner.
    ///
    /// NOTHING HERE MAY THROW -- <see cref="CommitAll"/>'s documented contract is that it never does, and
    /// the executor relies on that to report a commit failure alongside what did land instead of losing
    /// the run. Every adapter call is therefore either inside a try or routed through a Safe* helper,
    /// including the <c>CommitFailures</c> reads, which are ordinary property getters on an adapter and so
    /// can fail exactly like any other call into Revit.
    /// </summary>
    private static CommitAttempt AttemptCommit(Entry entry, List<FailureSummary> failures)
    {
        TransactionCommitResult result;
        try
        {
            result = entry.Transaction.Commit();
        }
        catch (Exception ex)
        {
            SafeCollectFailures(entry, failures);
            var transactionUnwound = SafeRollBack(entry.Transaction.RollBack);
            var groupUnwound = SafeRollBack(entry.Group.RollBack);
            return CommitAttempt.Failed(ex, transactionUnwound && groupUnwound);
        }

        SafeCollectFailures(entry, failures);

        if (result == TransactionCommitResult.RolledBack)
        {
            // Revit already rolled back the Transaction itself (ProceedWithRollBack) -- only the
            // TransactionGroup still needs an explicit rollback; calling Transaction.RollBack() again
            // here would be invalid. The Transaction half is therefore already undone by Revit, so the
            // group's own outcome is the whole answer for this document.
            var groupUnwound = SafeRollBack(entry.Group.RollBack);
            return CommitAttempt.Failed(new InvalidOperationException(SafeRollBackReason(entry)), groupUnwound);
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
            return CommitAttempt.Failed(ex, SafeRollBack(entry.Group.RollBack));
        }

        return CommitAttempt.Committed();
    }

    /// <summary>
    /// Appends this document's Failures-API summaries (PRD §07), never throwing. A getter that fails is
    /// itself reported as an error-severity failure rather than dropped -- it reaches the caller's
    /// notices[] through the same path every real Revit failure does, so PRD §01's
    /// observability-over-silence still holds for the case where the observability channel is what broke.
    /// </summary>
    private static void SafeCollectFailures(Entry entry, List<FailureSummary> failures)
    {
        try
        {
            failures.AddRange(entry.Transaction.CommitFailures);
        }
        catch (Exception ex)
        {
            failures.Add(new FailureSummary(
                isError: true,
                message: $"The Failures API result for '{SafeDescribe(entry)}' could not be read, so any Revit " +
                         $"warnings or errors raised while committing it are not listed here: {ex.Message}",
                failureDefinitionId: "mcp-bridge.commit-failures-unreadable",
                failingElementIds: Array.Empty<string>()));
        }
    }

    /// <summary>
    /// The message for a Revit-forced rollback (ProceedWithRollBack), or a description of why that
    /// message could not be recovered. Never throws -- see <see cref="AttemptCommit"/>.
    /// </summary>
    private static string SafeRollBackReason(Entry entry)
    {
        try
        {
            return entry.Transaction.CommitFailures.LastOrDefault(f => f.IsError)?.Message
                ?? "A transaction failure forced a rollback.";
        }
        catch (Exception ex)
        {
            return "A transaction failure forced a rollback, and the failure list naming it could not be " +
                   $"read either: {ex.Message}";
        }
    }

    /// <summary>Entry.Describe() without letting a failing Document.Title break error reporting.</summary>
    private static string SafeDescribe(Entry entry)
    {
        try
        {
            return entry.Describe();
        }
        catch
        {
            return "(a document whose title could not be read)";
        }
    }

    /// <summary>
    /// Rolls back best-effort and REPORTS WHETHER IT WORKED (independent PR review finding). Callers used
    /// to discard this, then add the document to the "rolled back" list unconditionally -- so a document
    /// whose own rollback threw was still reported as cleanly rolled back, which is precisely the claim
    /// the partial-commit report exists to get right, and the ambient document (a human's real open model)
    /// is one of the documents it could be wrong about.
    ///
    /// Still swallowing, deliberately: a rollback exception must never mask the original failure being
    /// reported, nor stop the next document's rollback. The bug was never that it caught -- it is that
    /// nobody asked what it caught.
    /// </summary>
    private static bool SafeRollBack(Action rollBack)
    {
        try
        {
            rollBack();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
