using System.Collections.Generic;
using MCPBridge.Core.Diagnostics;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Bridges a running script's per-dialog override policy (ScriptGlobals.DialogResultOverrides) to the
/// OnStartup-registered DialogBoxShowing handler in MCPBridge.AddIn, which has no other route to "the
/// ScriptGlobals of whichever script happens to be running right now." DialogBoxShowing fires
/// reentrant, synchronously, on the same UI-thread call stack a script's own Revit API calls run on
/// (ExternalEvent.Execute() -> RunScriptWorkItem -> TransactionScriptExecutor.ExecuteAsync ->
/// RoslynScriptRunner.RunAsync). A plain static is safe here specifically because ExecutionManager
/// guarantees at most one active (non-terminal) execution per Revit instance at a time -- there is
/// never more than one script's overrides live at once.
///
/// Two-way, not just an override lookup: every dialog the handler sees -- overridden by the script or
/// answered with the default-safe policy -- gets recorded here too (PRD §07 discussion: the agent
/// needs to see what Revit was asking, even though the add-in already answered on its behalf). Drained
/// once per script run by TransactionScriptExecutor and folded into that execution's notices[].
///
/// INTERNAL, AND THAT IS A SECURITY BOUNDARY, NOT A STYLE CHOICE (third-round review finding,
/// live-verified before and after). While this was a PUBLIC static class, a script could call these
/// members by name -- no reflection, no internal type -- and two things followed, both confirmed
/// live against Revit 2027 in a run that reported status "success":
///
///  - `ActiveDialogContext.ClearActive();` mid-script switched auto-suppression OFF (the AddIn's
///    DialogSuppressionHandler answers a dialog only while IsActive), so the next real modal dialog
///    would DISPLAY and block the very UI thread the script is running on -- wedging Revit with no
///    guarantee a human is present to click it. A denial of service in one line.
///  - `ActiveDialogContext.DrainRecorded();` mid-script emptied the recorded notices before
///    TransactionScriptExecutor drains them at the end of the run -- observed erasing a real
///    auto-answered dialog's record -- defeating PRD §01's "never handled invisibly" while the run
///    still reported success.
///
/// The read-only members (IsActive, TryGetOverride) went internal along with the mutators rather
/// than being kept public: nothing outside these assemblies wants them, and this codebase's rule is
/// that a public type in MCPBridge.Core/MCPBridge.RevitAdapter is a script-REACHABLE type
/// (RoslynScriptRunner.LoadableReferences() references every assembly loaded in the Revit AppDomain,
/// this one included). MCPBridge.AddIn's DialogSuppressionHandler and MCPBridge.Core.Tests keep
/// their access through the InternalsVisibleTo grants in MCPBridge.Core.csproj; a Roslyn script
/// submission assembly is never named there and cannot choose its own name. Pinned by
/// revit/test-harness/denylist_bypass_test.go (TestScriptCannotTamperWithDialogSuppression), with
/// TestDialogsAreStillAutoSuppressed pinning that the feature itself still works across that seam.
/// </summary>
internal static class ActiveDialogContext
{
    private static IDictionary<string, int>? _overrides;
    private static List<DiagnosticRecord> _recorded = new();

    /// <summary>
    /// True only while a script is actually running. Review finding: without this, the AddIn-side
    /// DialogBoxShowing handler had no way to tell "a script is running" apart from "Revit is idle with
    /// a human at the keyboard" -- it auto-answered every dialog in the entire Revit session
    /// unconditionally, including a human's own "Save changes?"/sync-with-central prompts. The handler
    /// must check this and let Revit show the dialog normally (no override at all) whenever it's false.
    /// </summary>
    public static bool IsActive => _overrides is not null;

    public static void SetActive(IDictionary<string, int> overrides)
    {
        _overrides = overrides;
        _recorded = new List<DiagnosticRecord>();
    }

    public static void ClearActive()
    {
        _overrides = null;
        _recorded = new List<DiagnosticRecord>();
    }

    /// <summary>The active script's override for dialogId, if any -- null means "use the handler's default-safe policy."</summary>
    public static int? TryGetOverride(string dialogId) =>
        _overrides is not null && _overrides.TryGetValue(dialogId, out var result) ? result : null;

    public static void RecordShown(DiagnosticRecord notice) => _recorded.Add(notice);

    public static IReadOnlyList<DiagnosticRecord> DrainRecorded()
    {
        var drained = _recorded;
        _recorded = new List<DiagnosticRecord>();
        return drained;
    }
}
