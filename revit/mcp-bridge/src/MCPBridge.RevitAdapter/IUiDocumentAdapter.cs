namespace MCPBridge.RevitAdapter;

/// <summary>
/// Thin seam over Autodesk.Revit.UI.UIDocument (PRD §06), used by RequestDispatcher to obtain the full
/// IDocumentAdapter it passes to TransactionScriptExecutor. NOT what a script itself sees -- that's
/// IScriptUiDocument (this interface's Document would let a script reach CreateTransaction through
/// `UIDocument.Document`, same problem IScriptDocument fixes for the top-level `Document` global).
/// </summary>
public interface IUiDocumentAdapter : IScriptUiDocument
{
    new IDocumentAdapter Document { get; }
}
