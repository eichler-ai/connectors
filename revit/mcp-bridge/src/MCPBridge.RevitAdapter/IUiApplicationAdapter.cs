namespace MCPBridge.RevitAdapter;

/// <summary>
/// Thin seam over Autodesk.Revit.UI.UIApplication (PRD §06), used by RequestDispatcher to obtain the full
/// IUiDocumentAdapter it needs. The real UIApplication a script binds to as its `UIApplication` global
/// (PRD §14) comes from <see cref="IRawUiApplicationSource"/>, which the real adapter also implements.
/// </summary>
public interface IUiApplicationAdapter
{
    /// <summary>The document active in the foreground when the script began running, if any.</summary>
    IUiDocumentAdapter? ActiveUiDocument { get; }
}
