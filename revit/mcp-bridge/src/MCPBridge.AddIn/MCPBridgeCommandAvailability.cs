using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace MCPBridge.AddIn;

/// <summary>
/// Keeps the "MCP Bridge" ribbon buttons enabled when NO document is open. Revit's default for an
/// external-command button is to grey it out until a document is active, which for these three
/// buttons is exactly backwards: Status, Reconnect, and the broker-mode switch are about the
/// connection to the broker, which exists (and can be wrong) before any document does -- and an empty
/// <c>instances[]</c> is most often investigated on a Revit sitting at its start page (independent PR
/// review finding, #187). None of the three commands touch the document.
/// </summary>
public sealed class MCPBridgeCommandAvailability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}
