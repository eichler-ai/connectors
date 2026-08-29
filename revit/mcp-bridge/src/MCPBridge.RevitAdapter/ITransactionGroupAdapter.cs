namespace MCPBridge.RevitAdapter;

/// <summary>Thin seam over Autodesk.Revit.DB.TransactionGroup (PRD §06).</summary>
internal interface ITransactionGroupAdapter
{
    void Start();
    void Assimilate();
    void RollBack();

    /// <summary>See <see cref="ITransactionAdapter.Dispose"/> -- same contract (post-terminal, idempotent, never throws out), same issue (#34).</summary>
    void Dispose();
}
