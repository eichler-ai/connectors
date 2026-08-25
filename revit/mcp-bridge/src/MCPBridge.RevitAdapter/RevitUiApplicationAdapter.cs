using Autodesk.Revit.UI;

namespace MCPBridge.RevitAdapter;

/// <summary>Real implementation wrapping Autodesk.Revit.UI.UIApplication. Not unit-tested (see RevitTransactionAdapter).</summary>
public sealed class RevitUiApplicationAdapter : IUiApplicationAdapter
{
    public RevitUiApplicationAdapter(UIApplication uiApplication)
    {
        ActiveUiDocument = uiApplication.ActiveUIDocument is { } doc
            ? new RevitUiDocumentAdapter(doc)
            : null;
    }

    public IUiDocumentAdapter? ActiveUiDocument { get; }

    IScriptUiDocument? IScriptUiApplication.ActiveUiDocument => ActiveUiDocument;
}
