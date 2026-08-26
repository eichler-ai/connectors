using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using MCPBridge.Core.Diagnostics;
using MCPBridge.Core.Execution;
using MCPBridge.RevitAdapter;

namespace MCPBridge.AddIn;

/// <summary>
/// Registers/unregisters UIControlledApplication.DialogBoxShowing (PRD §07). Default-safe auto-answer
/// (never a destructive default) unless the currently-running script opted into a different per-dialog
/// result via ScriptGlobals.DialogResultOverrides -- ActiveDialogContext is the only route from here to
/// "whichever script's ScriptGlobals happen to be live right now" (this class has no other reference to
/// it). Every dialog seen is also recorded (overridden or not) so its text survives back to the script
/// that (indirectly) triggered it, as a notice -- an agent needs to know what Revit was asking even
/// though the add-in already answered on its behalf.
/// </summary>
internal static class DialogSuppressionHandler
{
    internal static void Register(UIControlledApplication application, Action<string> logDiagnostic)
    {
        try
        {
            application.DialogBoxShowing += OnDialogBoxShowing;
        }
        catch (Exception ex)
        {
            logDiagnostic($"DialogSuppressionHandler.Register failed: {ex}");
        }
    }

    internal static void Unregister(UIControlledApplication application)
    {
        try
        {
            application.DialogBoxShowing -= OnDialogBoxShowing;
        }
        catch
        {
            // Best-effort on shutdown -- nothing meaningful to recover into at this point.
        }
    }

    private static void OnDialogBoxShowing(object? sender, DialogBoxShowingEventArgs e)
    {
        // Review finding: without this check, every dialog in the whole Revit session was auto-answered
        // unconditionally -- including a human's own "Save changes?"/sync-with-central prompts when no
        // script is running at all. Only intercept while a script is actually executing.
        if (!ActiveDialogContext.IsActive)
        {
            return;
        }

        var message = "(unknown dialog type)";
        var resultUsed = DialogResultDefaults.TaskDialogCancel;
        try
        {
            var (msg, defaultResult) = e switch
            {
                TaskDialogShowingEventArgs t => (t.Message, DialogResultDefaults.TaskDialogCancel),
                MessageBoxShowingEventArgs m => (m.Message, DialogResultDefaults.MessageBoxCancel),
                _ => ("(unknown dialog type)", DialogResultDefaults.TaskDialogCancel),
            };
            message = msg;
            var overrideResult = ActiveDialogContext.TryGetOverride(e.DialogId);
            resultUsed = overrideResult ?? defaultResult;
        }
        catch
        {
            // Fall through with the safe defaults above -- reading the dialog's own text/id must never
            // stop this handler from still answering it below.
        }

        try
        {
            // Review finding: OverrideResult must run BEFORE notice-building, and on its own try/catch --
            // a throw while building the diagnostic notice (e.g. DiagnosticRecord.Create's non-empty-
            // message guard, if a dialog's Message happens to be blank) must never leave the dialog
            // unanswered. An unanswered modal dialog wedges Revit's UI thread, which is exactly the
            // failure this feature exists to prevent.
            e.OverrideResult(resultUsed);
        }
        catch
        {
            return;
        }

        try
        {
            ActiveDialogContext.RecordShown(DiagnosticRecord.Create(
                DiagnosticSeverity.Info,
                "dialog-auto-answered",
                DiagnosticSource.Dialogs,
                $"Revit dialog auto-answered: {(string.IsNullOrWhiteSpace(message) ? "(no message text)" : message)}",
                detail: new Dictionary<string, object?>
                {
                    ["dialog_id"] = e.DialogId,
                    ["message"] = message,
                    ["override_result"] = resultUsed,
                },
                remedy: null));
        }
        catch
        {
            // Best-effort notice only -- the dialog is already answered above regardless of this outcome.
        }
    }
}
