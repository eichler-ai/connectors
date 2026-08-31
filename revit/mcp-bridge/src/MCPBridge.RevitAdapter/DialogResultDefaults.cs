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
/// AN UNOFFERED RESULT IS STILL ACCEPTED -- tested, because the enum list above makes it look like it
/// might not be. Cancel is only a meaningful ANSWER for a dialog that offers Cancel, so a TaskDialog
/// built with only Close (8), or only Yes/No (6/7), might plausibly ignore OverrideResult(2) and display
/// anyway, blocking the UI thread -- the exact failure this handler exists to prevent. It does not.
/// Measured live, Revit 2027, three dialogs raised from a script and answered by this handler:
///
///   offers Cancel (control) -> handler sent 2 -> Show() returned Cancel(2), never displayed
///   offers ONLY Close       -> handler sent 2 -> Show() returned Cancel(2), never displayed
///   offers ONLY Yes/No      -> handler sent 2 -> Show() returned Cancel(2), never displayed
///
/// So OverrideResult is not validated against the dialog's button set: it suppresses the dialog and
/// returns the value whether or not that button exists. TaskDialogCancel is therefore safe as a blanket
/// default for every intercepted TaskDialog, which is what this type assumes and what
/// DialogSuppressionHandler relies on. It also rules this out as the mechanism behind the non-framework
/// modals that wedge an instance -- those are genuinely never intercepted (PRD §07's window-inventory
/// fallback only fires when NO DialogBoxShowing event was raised), not intercepted and answered wrongly.
///
/// One thing the same runs incidentally showed, relevant to ScriptGlobals.DialogResultOverrides: a
/// TaskDialog constructed by a script reports DialogId "" (empty). That dictionary is keyed by DialogId,
/// so script-raised dialogs cannot be targeted individually through it -- only Revit's own dialogs, which
/// carry real ids, can. Not a defect, but it means an override key of "" is meaningless rather than a
/// wildcard.
/// </summary>
public static class DialogResultDefaults
{
    public const int TaskDialogCancel = 2;
    public const int MessageBoxCancel = 2;
}
