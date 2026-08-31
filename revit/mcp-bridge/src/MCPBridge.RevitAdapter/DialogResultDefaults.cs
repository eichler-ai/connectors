namespace MCPBridge.RevitAdapter;

/// <summary>
/// Raw OverrideResult(int) values for DialogBoxShowing's default-safe auto-answer (PRD §07).
/// DialogBoxShowingEventArgs.OverrideResult takes a plain int, not a typed enum, because one dispatch
/// point has to cover both TaskDialogShowingEventArgs (Autodesk.Revit.UI.TaskDialogResult) and
/// MessageBoxShowingEventArgs (framework message-box return codes) with one signature.
///
/// VERIFIED LIVE, Revit 2027, 2026-08-31 -- both constants, by reflecting over the real enums inside
/// the running Revit process (the check the original comment asked for and shipped without, because it
/// was written on a Mac with no RevitAPIUI.dll to read):
///
///   Autodesk.Revit.UI.TaskDialogResult   None=0 Ok=1 Cancel=2 Retry=4 Yes=6 No=7 Close=8
///                                        CommandLink1..4 = 1001..1004, underlying type Int32
///   System.Windows.Forms.DialogResult    None=0 OK=1 Cancel=2 Abort=3 Retry=4 Ignore=5 Yes=6 No=7
///                                        TryAgain=10 Continue=11
///
/// So TaskDialogCancel == TaskDialogResult.Cancel and MessageBoxCancel == DialogResult.Cancel (also the
/// native IDCANCEL), and MessageBoxCancel stays a raw int rather than a System.Windows.Forms.DialogResult
/// reference, to avoid flipping MCPBridge.AddIn.csproj's UseWindowsForms back to true (currently false,
/// deliberately, for the non-modal WPF status window).
///
/// KNOWN LIMIT, NOT YET TESTED -- and the enum list above is what makes it worth naming. Cancel is only
/// a meaningful answer for a dialog that OFFERS Cancel. A TaskDialog built with, say, only Close (8), or
/// only Yes/No (6/7), has no Cancel to return, and what OverrideResult(2) does there is unverified: it
/// may be ignored, leaving the dialog on screen and the UI thread blocked -- the exact failure
/// DialogSuppressionHandler exists to prevent. That is a plausible mechanism for a modal that survives
/// auto-answering, and it is worth establishing before the §15 phase-7 allowlist is built on top of this
/// default. Testing it means deliberately showing such a dialog on a live instance, so it was not done
/// opportunistically while another session was using the only Revit available.
/// </summary>
public static class DialogResultDefaults
{
    public const int TaskDialogCancel = 2;
    public const int MessageBoxCancel = 2;
}
