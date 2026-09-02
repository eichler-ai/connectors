using Autodesk.Revit.DB;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Real implementation wrapping Autodesk.Revit.DB.TransactionGroup. Not unit-tested, and internal, both
/// for the reasons on <see cref="RevitTransactionAdapter"/>.
/// </summary>
internal sealed class RevitTransactionGroupAdapter : ITransactionGroupAdapter
{
    private readonly TransactionGroup _group;
    private bool _disposed;

    public RevitTransactionGroupAdapter(TransactionGroup group)
    {
        _group = group;
    }

    /// <summary>See <see cref="RevitTransactionAdapter.Dispose"/> -- same contract and reasoning (issue #34).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _group.Dispose();
        }
        catch
        {
            // Swallow by contract -- see RevitTransactionAdapter.Dispose.
        }
    }

    public void Start() => _group.Start();

    public void Assimilate() => _group.Assimilate();

    public void RollBack() => _group.RollBack();

    public void SetName(string name) => _group.SetName(name);
}
