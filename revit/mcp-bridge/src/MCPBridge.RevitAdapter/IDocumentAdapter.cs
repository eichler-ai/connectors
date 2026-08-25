namespace MCPBridge.RevitAdapter;

/// <summary>
/// Thin seam over Autodesk.Revit.DB.Document (PRD §06/§09). Exposes just what
/// phase 01's script execution loop needs -- transaction creation and enough
/// identity to name things in diagnostics. Document identity hashing (§09) is
/// out of scope for phase 01 (that's the workspace/file-exchange work in phase 03).
/// </summary>
public interface IDocumentAdapter
{
    /// <summary>Human-readable title, for diagnostics only -- not a stable identity.</summary>
    string Title { get; }

    ITransactionAdapter CreateTransaction(string name);

    ITransactionGroupAdapter CreateTransactionGroup(string name);
}
