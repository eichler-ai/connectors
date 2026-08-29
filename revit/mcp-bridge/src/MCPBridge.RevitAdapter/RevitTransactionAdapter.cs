using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Real implementation wrapping Autodesk.Revit.DB.Transaction. Not unit-tested --
/// Revit API types are not constructible outside a live session (see the
/// revit-connector-development skill's testing strategy). Exercised only by the
/// live integration harness.
///
/// Internal alongside <see cref="RevitDocumentAdapter"/>. Constructing this already required a script to
/// construct the Autodesk.Revit.DB.Transaction it wraps, which ScriptApiDenylist check 1 refuses outright,
/// so this was never the open door -- but it is only ever built from that adapter, in this assembly, and
/// leaving one half of the pair public would just invite the question again.
/// </summary>
internal sealed class RevitTransactionAdapter : ITransactionAdapter
{
    private readonly Transaction _transaction;
    private bool _disposed;

    public RevitTransactionAdapter(Transaction transaction)
    {
        _transaction = transaction;
    }

    /// <summary>
    /// See <see cref="ITransactionAdapter.Dispose"/>. Idempotent via the flag; the underlying call is
    /// additionally swallowed because Revit's disposal semantics after a mid-failure unwind are
    /// undocumented, and a throw here must never mask the original failure being reported -- the
    /// object is finalizer-backed regardless, so a failed eager dispose only means the old
    /// (pre-#34) reclamation timing for that one object.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _transaction.Dispose();
        }
        catch
        {
            // Swallow by contract -- see the doc comment.
        }
    }

    public IReadOnlyList<FailureSummary> CommitFailures { get; private set; } = Array.Empty<FailureSummary>();

    public void Start() => _transaction.Start();

    public TransactionCommitResult Commit()
    {
        var preprocessor = new AdapterFailuresPreprocessor();
        var options = _transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(preprocessor);
        _transaction.SetFailureHandlingOptions(options);

        var status = _transaction.Commit();
        CommitFailures = preprocessor.Summaries;

        // Review finding: mapping every non-Committed status to RolledBack was wrong -- only
        // TransactionStatus.RolledBack means Revit already closed the Transaction itself (the
        // ProceedWithRollBack contract this class relies on); any other non-Committed status
        // (Uninitialized/Pending/Error/Started) means the Transaction is NOT actually closed, and the
        // caller must not skip its own RollBack() call the way it correctly does for RolledBack.
        return status switch
        {
            TransactionStatus.Committed => TransactionCommitResult.Committed,
            TransactionStatus.RolledBack => TransactionCommitResult.RolledBack,
            _ => throw new InvalidOperationException($"Transaction.Commit() returned unexpected status {status}."),
        };
    }

    public void RollBack() => _transaction.RollBack();
}
