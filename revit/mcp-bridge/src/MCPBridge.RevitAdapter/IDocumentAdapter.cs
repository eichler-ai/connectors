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
}
