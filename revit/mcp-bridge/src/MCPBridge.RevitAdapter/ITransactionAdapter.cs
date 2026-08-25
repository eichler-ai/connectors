namespace MCPBridge.RevitAdapter;

/// <summary>
/// Thin seam over Autodesk.Revit.DB.Transaction (PRD §06). Deliberately minimal for
/// phase 01: start/commit/roll back only. No IFailuresPreprocessor hookup here --
/// that's phase 02 (PRD §15).
/// </summary>
public interface ITransactionAdapter
{
    void Start();
    void Commit();
    void RollBack();
}
