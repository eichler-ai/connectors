using System;
using System.Collections.Generic;
using MCPBridge.Core.Diagnostics;

namespace MCPBridge.Core.Execution;

/// <summary>Result of running one script through RoslynScriptRunner -- never throws to the caller (PRD §06 step 4: exceptions populate the JSON-RPC error, they don't propagate as .NET exceptions past this boundary).</summary>
public sealed class ScriptExecutionOutcome
{
    public bool Success { get; init; }
    public bool WasCancelled { get; init; }
    public object? ReturnValue { get; init; }
    public string StdOut { get; init; } = "";
    public Exception? Exception { get; init; }

    /// <summary>Dialogs auto-answered (PRD §07) and transaction failures auto-resolved (warnings dismissed, errors rolled back) during this run -- folded into notices[] on the wire result.</summary>
    public IReadOnlyList<DiagnosticRecord> Notices { get; init; } = Array.Empty<DiagnosticRecord>();

    public static ScriptExecutionOutcome Completed(object? returnValue, string stdOut, IReadOnlyList<DiagnosticRecord>? notices = null) =>
        new() { Success = true, ReturnValue = returnValue, StdOut = stdOut, Notices = notices ?? Array.Empty<DiagnosticRecord>() };

    public static ScriptExecutionOutcome Failed(Exception exception, string stdOut, IReadOnlyList<DiagnosticRecord>? notices = null) =>
        new() { Success = false, Exception = exception, StdOut = stdOut, Notices = notices ?? Array.Empty<DiagnosticRecord>() };

    public static ScriptExecutionOutcome Cancelled(string stdOut, IReadOnlyList<DiagnosticRecord>? notices = null) =>
        new() { Success = false, WasCancelled = true, StdOut = stdOut, Notices = notices ?? Array.Empty<DiagnosticRecord>() };
}
