namespace MCPBridge.RevitAdapter;

/// <summary>Thin seam over Autodesk.Revit.DB.TransactionGroup (PRD §06).</summary>
internal interface ITransactionGroupAdapter
{
    void Start();
    void Assimilate();
    void RollBack();
}
