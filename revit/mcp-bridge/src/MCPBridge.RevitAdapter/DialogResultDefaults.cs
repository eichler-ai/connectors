namespace MCPBridge.RevitAdapter;

/// <summary>
/// Raw OverrideResult(int) values for DialogBoxShowing's default-safe auto-answer (PRD §07).
/// DialogBoxShowingEventArgs.OverrideResult takes a plain int, not a typed enum, because one dispatch
/// point has to cover both TaskDialogShowingEventArgs (Autodesk.Revit.UI.TaskDialogResult) and
/// MessageBoxShowingEventArgs (framework message-box return codes) with one signature.
///
/// NEEDS LIVE VERIFICATION before merge (cannot be checked from the Mac dev machine this was written
/// on -- no RevitAPIUI.dll available): TaskDialogCancel assumes TaskDialogResult.Cancel == 2, the
/// published value across Revit API versions. MessageBoxCancel is deliberately a raw int (assumed 2,
/// matching both System.Windows.Forms.DialogResult.Cancel and the native IDCANCEL) rather than a
/// System.Windows.Forms.DialogResult reference, to avoid flipping MCPBridge.AddIn.csproj's
/// UseWindowsForms back to true (currently false, deliberately, for the non-modal WPF status window).
/// </summary>
public static class DialogResultDefaults
{
    public const int TaskDialogCancel = 2;
    public const int MessageBoxCancel = 2;
}
