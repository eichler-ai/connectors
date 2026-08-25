using Autodesk.Revit.DB;

namespace MCPBridge.RevitAdapter;

/// <summary>Real implementation wrapping Autodesk.Revit.DB.Document. Not unit-tested (see RevitTransactionAdapter).</summary>
public sealed class RevitDocumentAdapter : IDocumentAdapter
{
    private readonly Document _document;

    public RevitDocumentAdapter(Document document)
    {
        _document = document;
    }

    public string Title => _document.Title;

    public ITransactionAdapter CreateTransaction(string name) =>
        new RevitTransactionAdapter(new Transaction(_document, name));

    public ITransactionGroupAdapter CreateTransactionGroup(string name) =>
        new RevitTransactionGroupAdapter(new TransactionGroup(_document, name));
}
