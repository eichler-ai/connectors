namespace MCPBridge.RevitAdapter;

/// <summary>Thin seam over Autodesk.Revit.DB.TransactionGroup (PRD §06).</summary>
internal interface ITransactionGroupAdapter
{
    void Start();
    void Assimilate();
    void RollBack();

    /// <summary>
    /// Renames the group before it is assimilated (#146 Phase 2b): the assimilated group's name is the
    /// entry a person sees in Revit's Undo history, so this is what makes the connector's undo entry
    /// readable ("MCP: 12 Walls created") instead of the fixed "MCP Bridge Script".
    /// </summary>
    void SetName(string name);

    /// <summary>See <see cref="ITransactionAdapter.Dispose"/> -- same contract (post-terminal, idempotent, never throws out), same issue (#34).</summary>
    void Dispose();
}
