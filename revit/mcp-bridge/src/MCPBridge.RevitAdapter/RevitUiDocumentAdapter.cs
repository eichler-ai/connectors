using Autodesk.Revit.UI;

namespace MCPBridge.RevitAdapter;

/// <summary>Real implementation wrapping Autodesk.Revit.UI.UIDocument. Not unit-tested (see RevitTransactionAdapter).</summary>
public sealed class RevitUiDocumentAdapter : IUiDocumentAdapter
{
    public RevitUiDocumentAdapter(UIDocument uiDocument)
    {
        Document = new RevitDocumentAdapter(uiDocument.Document);
    }

    public IDocumentAdapter Document { get; }

    IScriptDocument IScriptUiDocument.Document => Document;
}
