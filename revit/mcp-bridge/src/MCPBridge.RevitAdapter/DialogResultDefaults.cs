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
/// SENDING A RESULT THE DIALOG DOES NOT OFFER STILL SUPPRESSES IT -- worth knowing, because the enum
/// list makes it look like it might not, and because Autodesk documents the opposite: OverrideResult's
/// own remarks say the id "must be relevant to the buttons in a message box", and for TaskDialog that
/// ids are accepted "depending on the buttons used in a dialog". Measured against that, Revit 2027,
/// three TaskDialogs raised FROM A SCRIPT and answered by DialogSuppressionHandler:
///
///   offers Cancel (control) -> handler sent 2 -> Show() returned Cancel(2), never displayed
///   offers ONLY Close       -> handler sent 2 -> Show() returned Cancel(2), never displayed
///   offers ONLY Yes/No      -> handler sent 2 -> Show() returned Cancel(2), never displayed
///
/// Scope that claim exactly as measured: script-raised TaskDialogs, one Revit build, against a
/// documented contract that says otherwise -- so this is observed permissiveness, not a guarantee to
/// rely on. It does NOT cover MessageBoxShowingEventArgs (no message box was ever intercepted;
/// MessageBoxCancel is verified only as an enum value above, and Autodesk's text for that branch is the
/// most restrictive of the three), and it does not establish that an unoffered result is SAFE for
/// Revit's OWN dialogs: in all three runs the caller of Show() was the script, which just received an
/// int. A Revit-internal dialog's result is consumed by Revit's command code, which branches on it, and
/// what that code does with a 2 it never offered is untested.
///
/// What the runs do establish, which is all DialogSuppressionHandler needs: the dialog is suppressed
/// and never blocks the UI thread, whatever buttons it declares.
///
/// See caveats.md's misdiagnosis table for what this ruled out and what it did NOT -- in particular,
/// it does not explain the modals that wedge an instance, and the leading remaining candidate is the
/// handler's own deliberate `if (!ActiveDialogContext.IsActive) return;` gate, which leaves a framework
/// dialog raised BETWEEN runs displayed and unanswered.
/// </summary>
public static class DialogResultDefaults
{
    public const int TaskDialogCancel = 2;
    public const int MessageBoxCancel = 2;
}
