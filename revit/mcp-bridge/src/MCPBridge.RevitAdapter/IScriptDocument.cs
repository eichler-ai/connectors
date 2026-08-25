namespace MCPBridge.RevitAdapter;

/// <summary>
/// What a script sees as `Document` (PRD §06's public script-globals contract). Deliberately narrower
/// than <see cref="IDocumentAdapter"/>: TransactionScriptExecutor already opens a Transaction/
/// TransactionGroup around every script run and commits/rolls back on the script's behalf, so a script
/// calling CreateTransaction itself would try to open a second Transaction on the same Document --
/// Revit only allows one open Transaction per Document at a time (true nesting needs SubTransaction,
/// out of scope for phase 01) -- and always fail with "Starting a new transaction is not permitted."
/// Confirmed live against a real Revit session, not a hypothetical: IDocumentAdapter.CreateTransaction
/// was exposed to scripts via ScriptGlobals.Document before this split, and any script that called it
/// (exactly as IDocumentAdapter's own doc comment told them to) failed every time.
/// </summary>
public interface IScriptDocument
{
    /// <summary>Human-readable title, for diagnostics only -- not a stable identity.</summary>
    string Title { get; }
}
