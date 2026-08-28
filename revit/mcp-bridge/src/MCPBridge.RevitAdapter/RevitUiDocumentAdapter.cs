using Autodesk.Revit.UI;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Real implementation wrapping Autodesk.Revit.UI.UIDocument. Not unit-tested (see RevitTransactionAdapter).
/// Internal for the same reason as <see cref="RevitDocumentAdapter"/>: it hands out one, so a script able
/// to construct this could reach that type's transaction factories through it.
/// </summary>
internal sealed class RevitUiDocumentAdapter : IUiDocumentAdapter, IRawUiDocumentSource
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
