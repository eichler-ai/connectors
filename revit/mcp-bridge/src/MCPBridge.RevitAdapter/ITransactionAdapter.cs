using System;
using System.Collections.Generic;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Thin seam over Autodesk.Revit.DB.Transaction (PRD §06/§07). Commit() returns
/// TransactionCommitResult (not void) so a caller can tell "committed" apart from "Revit auto-rolled
/// back the Transaction itself" (see that enum's own doc comment). SetFailuresObserver registers a
/// callback the adapter invokes once, synchronously, from inside Commit() if the Failures API surfaces
/// anything -- resolution policy (dismiss warnings, force rollback on any error) is fixed and applied
/// by the adapter itself; the observer exists purely so the caller can build notices[].
/// </summary>
public interface ITransactionAdapter
{
    void Start();
    TransactionCommitResult Commit();
    void RollBack();

    /// <summary>Must be called before Commit() to take effect.</summary>
    void SetFailuresObserver(Action<IReadOnlyList<FailureSummary>> observer);
}
