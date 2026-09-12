using Rhino.MCPBridge.Core.Diagnostics;

namespace Rhino.MCPBridge.Core.Execution;

/// <summary>Maps a finished <see cref="ScriptExecutionOutcome"/> onto the execution record with the
/// §01 codes each failure class gets (ported from the Revit dispatcher's CompleteExecutionAsError).</summary>
internal static class CompleteExecution
{
    public static void Apply(ExecutionManager manager, string executionId, DateTimeOffset now, ScriptExecutionOutcome outcome)
    {
        if (outcome.WasCancelled)
        {
            manager.CompleteCancelled(executionId, now, outcome.StdOut, outcome.Notices, outcome.Files, outcome.Grasshopper);
            return;
        }

        if (outcome.Success)
        {
            string? formatted;
            try { formatted = outcome.ReturnValue is null ? null : ReturnValueFormatter.Format(outcome.ReturnValue); }
            catch (Exception ex) { formatted = "<return value could not be formatted: " + ex.Message + ">"; }
            manager.CompleteSuccess(executionId, now, formatted, outcome.StdOut, outcome.Notices, outcome.Files, outcome.Mutations, outcome.Grasshopper);
            return;
        }

        var ex0 = outcome.Exception!;
        DiagnosticRecord record = ex0 switch
        {
            DocumentNotFoundException dnf => dnf.Record,
            GrasshopperDocumentNotFoundException gnf => gnf.Record,
            Microsoft.CodeAnalysis.Scripting.CompilationErrorException cex => DiagnosticRecord.Create(DiagnosticSeverity.Error, "script-compilation-failed", DiagnosticSource.Execution,
                cex.Message, new Dictionary<string, object?> { ["execution_id"] = executionId },
                new[] { "Fix the script. The scope has exactly four globals: Document (Rhino.RhinoDoc), CancellationToken, Connector, and GrasshopperDocument (an object -- cast it to Grasshopper.Kernel.GH_Document when you passed a gh_document_id); only System is imported, so qualify Rhino types (Rhino.Geometry.Sphere) or add a using at the top." }),
            Python.PythonScriptException py when py.IsSyntaxError => DiagnosticRecord.Create(DiagnosticSeverity.Error, "script-compilation-failed", DiagnosticSource.Execution,
                py.Message, new Dictionary<string, object?> { ["execution_id"] = executionId, ["traceback"] = py.Traceback },
                new[] { "Fix the script. Line numbers in the traceback refer to the script as sent. The scope has four globals: doc (Rhino.RhinoDoc), ghdoc, connector, cancel; assign `result` to return a value." }),
            Python.PythonScriptException py => DiagnosticRecord.Create(DiagnosticSeverity.Error, "script-execution-failed", DiagnosticSource.Execution,
                py.Message, new Dictionary<string, object?> { ["execution_id"] = executionId, ["exception_type"] = py.InnerException?.GetType().FullName, ["traceback"] = py.Traceback },
                new[] { "Read the traceback: it is what the script or Rhino's API raised. Check the member's signature with describe_function before retrying." }),
            ScriptApiDenylistViolationException den => DiagnosticRecord.Create(DiagnosticSeverity.Error, den.Code, DiagnosticSource.Execution,
                den.Message, new Dictionary<string, object?> { ["execution_id"] = executionId, ["member"] = den.DeniedMember },
                den.Code == ScriptApiDenylistViolationException.ConfirmationRequiredCode
                    ? new[] { "If this is genuinely intended, resend the identical execute_script call with confirm_lifecycle_actions: true; otherwise remove the call." }
                    : new[] { "Change the script; no argument lifts this refusal." }),
            ScriptAwaitNotAllowedException aw => DiagnosticRecord.Create(DiagnosticSeverity.Error, ScriptAwaitNotAllowedException.Code, DiagnosticSource.Execution,
                aw.Message, new Dictionary<string, object?> { ["execution_id"] = executionId }, new[] { "Remove the top-level await; call .GetAwaiter().GetResult() on the task instead." }),
            _ => DiagnosticRecord.Create(DiagnosticSeverity.Error, "script-execution-failed", DiagnosticSource.Execution,
                $"{ex0.GetType().FullName}: {ex0.Message}", new Dictionary<string, object?> { ["execution_id"] = executionId, ["exception_type"] = ex0.GetType().FullName, ["stack"] = ex0.StackTrace },
                new[] { "Read the exception: it is what Rhino's API threw. Check the member's signature with describe_function before retrying." }),
        };
        manager.CompleteError(executionId, now, record, outcome.StdOut, outcome.Notices, outcome.Files, outcome.Grasshopper);
    }
}
