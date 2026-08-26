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
        try
        {
            var (message, defaultResult) = e switch
            {
                TaskDialogShowingEventArgs t => (t.Message, DialogResultDefaults.TaskDialogCancel),
                MessageBoxShowingEventArgs m => (m.Message, DialogResultDefaults.MessageBoxCancel),
                _ => ("(unknown dialog type)", DialogResultDefaults.TaskDialogCancel),
            };

            var overrideResult = ActiveDialogContext.TryGetOverride(e.DialogId);
            var resultUsed = overrideResult ?? defaultResult;

            ActiveDialogContext.RecordShown(DiagnosticRecord.Create(
                DiagnosticSeverity.Info,
                "dialog-auto-answered",
                DiagnosticSource.Dialogs,
                $"Revit dialog auto-answered: {message}",
                detail: new Dictionary<string, object?>
                {
                    ["dialog_id"] = e.DialogId,
                    ["message"] = message,
                    ["override_result"] = resultUsed,
                    ["script_override"] = overrideResult is not null,
                },
                remedy: null));

            e.OverrideResult(resultUsed);
        }
        catch
        {
            // Swallowed deliberately: this runs on Revit's UI thread inside DialogBoxShowing's own
            // dispatch (PRD §01 observability principle notwithstanding) -- an uncaught exception here
            // is a crash-class risk, and there is no safe logging path guaranteed not to itself throw
            // from this callback. Worst case the dialog is left unanswered by this handler and the v1
            // window-inventory fallback (§07) picks it up on the next poll timeout instead.
        }
    }
}
