namespace MCPBridge.RevitAdapter;

/// <summary>
/// Thin seam over Autodesk.Revit.UI.ExternalEvent.Raise() (PRD §06). Core calls this
/// to wake Revit's idle loop; it never touches ExternalEvent itself.
/// </summary>
public interface IExternalEventRaiser
{
    void Raise();
}
