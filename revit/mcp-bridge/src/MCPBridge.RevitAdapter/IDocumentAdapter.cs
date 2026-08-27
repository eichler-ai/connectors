namespace MCPBridge.RevitAdapter;

/// <summary>
/// Thin seam over Autodesk.Revit.DB.Document (PRD §06/§09), used by TransactionScriptExecutor to build
/// the ambient Transaction/TransactionGroup it wraps every script run in. NOT what a script itself sees
/// -- that's the narrower <see cref="IScriptDocument"/> (this interface's CreateTransaction/
/// CreateTransactionGroup would always fail if a script called them directly, since the executor has
/// already opened one; see IScriptDocument's doc comment). Document identity hashing (§09) is out of
/// scope for phase 01 (that's the workspace/file-exchange work in phase 03).
/// </summary>
public interface IDocumentAdapter : IScriptDocument
{
    ITransactionAdapter CreateTransaction(string name);

    ITransactionGroupAdapter CreateTransactionGroup(string name);

    /// <summary>Local file path if this document has been saved, else null (PRD §09 document identity).</summary>
    string? PathName { get; }

    /// <summary>Whether this document is workshared (local or cloud/ACC central) -- PRD §09.</summary>
    bool IsWorkshared { get; }

    /// <summary>
    /// The user-visible central model path when <see cref="IsWorkshared"/> is true, else null.
    /// Never the local copy's path -- PRD §09: "per-user and regenerated on every fresh local copy".
    /// </summary>
    string? CentralModelPath { get; }
}
