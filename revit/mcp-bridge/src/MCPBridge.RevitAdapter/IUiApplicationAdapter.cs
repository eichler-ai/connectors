namespace MCPBridge.RevitAdapter;

/// <summary>Thin seam over Autodesk.Revit.UI.UIApplication (PRD §06).</summary>
public interface IUiApplicationAdapter
{
    /// <summary>The document active in the foreground when the script began running, if any.</summary>
    IUiDocumentAdapter? ActiveUiDocument { get; }
}
