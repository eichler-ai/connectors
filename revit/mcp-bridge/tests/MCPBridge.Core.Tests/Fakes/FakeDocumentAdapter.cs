using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Tests.Fakes;

/// <summary>Fake behind the RevitAdapter seam (per the revit-connector-development skill's testing strategy) -- records calls instead of touching a live Document.</summary>
public sealed class FakeDocumentAdapter : IDocumentAdapter
{
    public string Title { get; init; } = "FakeDocument";
    public FakeTransactionAdapter? LastTransaction { get; private set; }
    public FakeTransactionGroupAdapter? LastTransactionGroup { get; private set; }

    public ITransactionAdapter CreateTransaction(string name)
    {
        var tx = new FakeTransactionAdapter(name);
        LastTransaction = tx;
        return tx;
    }

    public ITransactionGroupAdapter CreateTransactionGroup(string name)
    {
        var group = new FakeTransactionGroupAdapter(name);
        LastTransactionGroup = group;
        return group;
    }
}
