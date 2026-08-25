namespace MCPBridge.RevitAdapter;

/// <summary>Thin seam over Autodesk.Revit.UI.UIDocument (PRD §06).</summary>
public interface IUiDocumentAdapter
{
    IDocumentAdapter Document { get; }
}
