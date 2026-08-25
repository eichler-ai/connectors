using System;

namespace MCPBridge.Core.Execution;

/// <summary>Result of running one script through RoslynScriptRunner -- never throws to the caller (PRD §06 step 4: exceptions populate the JSON-RPC error, they don't propagate as .NET exceptions past this boundary).</summary>
public sealed class ScriptExecutionOutcome
{
    public bool Success { get; init; }
    public bool WasCancelled { get; init; }
    public object? ReturnValue { get; init; }
    public string StdOut { get; init; } = "";
    public Exception? Exception { get; init; }

    public static ScriptExecutionOutcome Completed(object? returnValue, string stdOut) =>
        new() { Success = true, ReturnValue = returnValue, StdOut = stdOut };

    public static ScriptExecutionOutcome Failed(Exception exception, string stdOut) =>
        new() { Success = false, Exception = exception, StdOut = stdOut };

    public static ScriptExecutionOutcome Cancelled(string stdOut) =>
        new() { Success = false, WasCancelled = true, StdOut = stdOut };
}
