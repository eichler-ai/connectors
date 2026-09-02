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

    /// <summary>
    /// Files published via ScriptGlobals.Publish during this run (PRD §09) -- a sibling list to
    /// Notices, never conditional on Success: a script that publishes a file and then throws, is
    /// cancelled, or fails to commit still reports that file here.
    /// </summary>
    public IReadOnlyList<PublishedFileRecord> Files { get; init; } = Array.Empty<PublishedFileRecord>();

    /// <summary>
    /// #146 Phase 2: what the run changed, net, across its committed transactions -- or null when nothing
    /// did, or when the run failed (its changes were rolled back, so there is nothing to report). Unlike
    /// <see cref="Files"/> this IS conditional on success, by design: a report of writes that were undone
    /// would be the exact thing an agent must not act on.
    /// </summary>
    public MutationReport? Mutations { get; init; }

    public static ScriptExecutionOutcome Completed(object? returnValue, string stdOut, IReadOnlyList<DiagnosticRecord>? notices = null, IReadOnlyList<PublishedFileRecord>? files = null, MutationReport? mutations = null) =>
        new() { Success = true, ReturnValue = returnValue, StdOut = stdOut, Notices = notices ?? Array.Empty<DiagnosticRecord>(), Files = files ?? Array.Empty<PublishedFileRecord>(), Mutations = mutations };

    public static ScriptExecutionOutcome Failed(Exception exception, string stdOut, IReadOnlyList<DiagnosticRecord>? notices = null, IReadOnlyList<PublishedFileRecord>? files = null) =>
        new() { Success = false, Exception = exception, StdOut = stdOut, Notices = notices ?? Array.Empty<DiagnosticRecord>(), Files = files ?? Array.Empty<PublishedFileRecord>() };

    public static ScriptExecutionOutcome Cancelled(string stdOut, IReadOnlyList<DiagnosticRecord>? notices = null, IReadOnlyList<PublishedFileRecord>? files = null) =>
        new() { Success = false, WasCancelled = true, StdOut = stdOut, Notices = notices ?? Array.Empty<DiagnosticRecord>(), Files = files ?? Array.Empty<PublishedFileRecord>() };
}
