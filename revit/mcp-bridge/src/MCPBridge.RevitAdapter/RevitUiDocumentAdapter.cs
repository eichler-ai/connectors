using Autodesk.Revit.UI;

namespace MCPBridge.RevitAdapter;

/// <summary>Real implementation wrapping Autodesk.Revit.UI.UIDocument. Not unit-tested (see RevitTransactionAdapter).</summary>
public sealed class RevitUiDocumentAdapter : IUiDocumentAdapter, IRawUiDocumentSource
{
    private readonly UIDocument _uiDocument;

    public RevitUiDocumentAdapter(UIDocument uiDocument)
    {
        _uiDocument = uiDocument;
        Document = new RevitDocumentAdapter(uiDocument.Document);
    }

    public IDocumentAdapter Document { get; }

    /// <summary>The real UIDocument this adapter wraps (PRD §14) -- see IDocumentAdapter.RawDocument.</summary>
    public UIDocument RawUiDocument => _uiDocument;
}
