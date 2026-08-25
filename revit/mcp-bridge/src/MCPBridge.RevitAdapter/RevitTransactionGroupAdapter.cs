using Autodesk.Revit.DB;

namespace MCPBridge.RevitAdapter;

/// <summary>Real implementation wrapping Autodesk.Revit.DB.TransactionGroup. Not unit-tested (see RevitTransactionAdapter).</summary>
public sealed class RevitTransactionGroupAdapter : ITransactionGroupAdapter
{
    private readonly TransactionGroup _group;

    public RevitTransactionGroupAdapter(TransactionGroup group)
    {
        _group = group;
    }

    public void Start() => _group.Start();

    public void Assimilate() => _group.Assimilate();

    public void RollBack() => _group.RollBack();
}
