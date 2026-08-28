using Autodesk.Revit.UI;

namespace MCPBridge.RevitAdapter;

/// <summary>Real implementation wrapping Autodesk.Revit.UI.UIApplication. Not unit-tested (see RevitTransactionAdapter).</summary>
public sealed class RevitUiApplicationAdapter : IUiApplicationAdapter, IRawUiApplicationSource
{
    private readonly UIApplication _uiApplication;

    public RevitUiApplicationAdapter(UIApplication uiApplication)
    {
        _uiApplication = uiApplication;
        ActiveUiDocument = uiApplication.ActiveUIDocument is { } doc
            ? new RevitUiDocumentAdapter(doc)
            : null;
    }

    public IUiDocumentAdapter? ActiveUiDocument { get; }

    /// <summary>The real UIApplication this adapter wraps (PRD §14) -- see IDocumentAdapter.RawDocument.</summary>
    public UIApplication RawUiApplication => _uiApplication;
}
