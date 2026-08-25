namespace MCPBridge.RevitAdapter;

/// <summary>
/// Thin seam over Autodesk.Revit.UI.UIApplication (PRD §06), used by RequestDispatcher to obtain the full
/// IUiDocumentAdapter it needs. NOT what a script itself sees -- that's IScriptUiApplication (see its own
/// doc comment).
/// </summary>
public interface IUiApplicationAdapter : IScriptUiApplication
{
    /// <summary>The document active in the foreground when the script began running, if any.</summary>
    new IUiDocumentAdapter? ActiveUiDocument { get; }
}
