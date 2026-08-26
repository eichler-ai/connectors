namespace MCPBridge.RevitAdapter;

/// <summary>
/// Whether Commit() actually committed or Revit auto-rolled back the Transaction itself as part of the
/// Failures API's ProceedWithRollBack contract (PRD §07) -- a plain void Commit() can't distinguish
/// these, and the caller needs to know: on RolledBack, the Transaction is already rolled back (calling
/// RollBack() again is invalid), only the TransactionGroup still needs an explicit rollback.
/// </summary>
public enum TransactionCommitResult
{
    Committed,
    RolledBack,
}
