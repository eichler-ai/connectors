using Autodesk.Revit.DB;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Real implementation wrapping Autodesk.Revit.DB.TransactionGroup. Not unit-tested, and internal, both
/// for the reasons on <see cref="RevitTransactionAdapter"/>.
/// </summary>
internal sealed class RevitTransactionGroupAdapter : ITransactionGroupAdapter
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
