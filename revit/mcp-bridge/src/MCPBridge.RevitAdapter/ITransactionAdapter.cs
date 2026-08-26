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
public interface ITransactionAdapter
{
    void Start();
    TransactionCommitResult Commit();
    void RollBack();

    IReadOnlyList<FailureSummary> CommitFailures { get; }
}
