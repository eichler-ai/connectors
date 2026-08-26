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
/// </summary>
public static class ActiveDialogContext
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
