using System.Collections.Generic;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Thin seam over Autodesk.Revit.DB.Transaction (PRD §06/§07). Commit() returns
/// TransactionCommitResult (not void) so a caller can tell "committed" apart from "Revit auto-rolled
/// back the Transaction itself" (see that enum's own doc comment). Resolution policy (dismiss warnings,
/// force rollback on any error) is fixed and applied by the adapter itself, via the Failures API;
/// CommitFailures exposes what it saw so the caller can build notices[] -- read only after Commit()
/// returns (empty before that), and read outside Revit's own failure-handling callback, not from
/// inside it (review finding: an observer invoked synchronously from inside the Failures API's own
/// dispatch could throw and leave the commit in an undefined state).
/// </summary>
internal interface ITransactionAdapter
{
    void Start();
    TransactionCommitResult Commit();
    void RollBack();

    /// <summary>
    /// Releases the wrapped Revit transaction's native resources (issue #34): the Revit API's
    /// Transaction is IDisposable, and leaving reclamation to finalizers delayed native memory
    /// release by one group+transaction pair per touched document per run -- a contributor to issue
    /// #31's growth. Called by ManagedDocumentTransactions strictly AFTER the entry's terminal
    /// handling (commit/rollback settled, failures read); must be idempotent and must never throw
    /// out (a dispose failure cannot be allowed to mask an original commit/rollback failure).
    /// Deliberately a plain member rather than the interface extending IDisposable -- nothing here
    /// should ever be `using`-scoped (the lifetime is the run's unwind machinery, not a block), and
    /// keeping IDisposable off the seam means the compiler can't quietly synthesize a disposal path
    /// the unwind ordering comments don't know about.
    /// </summary>
    void Dispose();

    IReadOnlyList<FailureSummary> CommitFailures { get; }
}
