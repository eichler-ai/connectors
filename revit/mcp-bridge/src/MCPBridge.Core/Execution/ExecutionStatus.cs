using System.Text.Json.Serialization;
using MCPBridge.Core.Protocol;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Terminal and non-terminal states for one execution (PRD §06). "busy" is
/// deliberately not a member here -- it's a response shape returned when a second
/// execute_script hits an instance that already has one of these in flight, not a
/// status stored on the execution itself.
/// </summary>
// Wire values must match the Go broker's Status type (execution.go) exactly: lowercase,
// and "success" rather than "completed" -- WireEnumName lets the wire representation
// diverge from the C# member name without renaming the identifiers themselves (which
// would touch a large number of already-reviewed call sites/tests for no behavioral
// benefit beyond the wire spelling). "busy" is deliberately not a member here -- see the
// class doc comment above.
[JsonConverter(typeof(WireEnumNameConverter<ExecutionStatus>))]
public enum ExecutionStatus
{
    /// <summary>Queued -- ExternalEvent.Raise() has been called but Execute() hasn't been entered yet (PRD §06).</summary>
    [WireEnumName("pending")]
    Pending,

    /// <summary>Execute() has been entered and the script is actually running on the UI thread.</summary>
    [WireEnumName("running")]
    Running,

    /// <summary>Wire value "success" (not "completed") -- matches the Go broker's StatusSuccess.</summary>
    [WireEnumName("success")]
    Completed,

    [WireEnumName("error")]
    Error,

    /// <summary>The script observed the cancellation token and unwound cleanly (PRD §06) -- distinct from Error since the agent asked for this.</summary>
    [WireEnumName("cancelled")]
    Cancelled,

    /// <summary>Cancellation's grace period lapsed without the script actually stopping (PRD §06). Sticky until Revit restarts.</summary>
    [WireEnumName("unrecoverable")]
    Unrecoverable,
}

public static class ExecutionStatusExtensions
{
    public static bool IsTerminal(this ExecutionStatus status) => status is
        ExecutionStatus.Completed or
        ExecutionStatus.Error or
        ExecutionStatus.Cancelled or
        ExecutionStatus.Unrecoverable;
}
