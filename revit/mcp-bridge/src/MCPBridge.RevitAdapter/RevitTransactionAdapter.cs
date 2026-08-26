using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Real implementation wrapping Autodesk.Revit.DB.Transaction. Not unit-tested --
/// Revit API types are not constructible outside a live session (see the
/// revit-connector-development skill's testing strategy). Exercised only by the
/// live integration harness.
/// </summary>
public sealed class RevitTransactionAdapter : ITransactionAdapter
{
    private readonly Transaction _transaction;
    private Action<IReadOnlyList<FailureSummary>>? _observer;

    public RevitTransactionAdapter(Transaction transaction)
    {
        _transaction = transaction;
    }

    public void Start() => _transaction.Start();

    public void SetFailuresObserver(Action<IReadOnlyList<FailureSummary>> observer) => _observer = observer;

    public TransactionCommitResult Commit()
    {
        if (_observer is not null)
        {
            var options = _transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new AdapterFailuresPreprocessor(_observer));
            _transaction.SetFailureHandlingOptions(options);
        }

        var status = _transaction.Commit();
        return status == TransactionStatus.Committed ? TransactionCommitResult.Committed : TransactionCommitResult.RolledBack;
    }

    public void RollBack() => _transaction.RollBack();
}
