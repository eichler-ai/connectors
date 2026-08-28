namespace MCPBridge.RevitAdapter;

/// <summary>
/// Thin seam over Autodesk.Revit.UI.UIDocument (PRD §06), used by RequestDispatcher to obtain the full
/// IDocumentAdapter it passes to TransactionScriptExecutor. The real UIDocument a script binds to as its
/// `UIDocument` global (PRD §14) comes from <see cref="IRawUiDocumentSource"/>, which the real adapter
/// also implements.
/// </summary>
internal interface IUiDocumentAdapter
{
    IDocumentAdapter Document { get; }
}
