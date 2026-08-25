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

    public RevitTransactionAdapter(Transaction transaction)
    {
        _transaction = transaction;
    }

    public void Start() => _transaction.Start();

    public void Commit() => _transaction.Commit();

    public void RollBack() => _transaction.RollBack();
}
