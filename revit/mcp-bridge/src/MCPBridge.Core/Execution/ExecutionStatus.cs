using System.Text.Json.Serialization;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Terminal and non-terminal states for one execution (PRD §06). "busy" is
/// deliberately not a member here -- it's a response shape returned when a second
/// execute_script hits an instance that already has one of these in flight, not a
/// status stored on the execution itself.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionStatus
{
    /// <summary>Queued -- ExternalEvent.Raise() has been called but Execute() hasn't been entered yet (PRD §06).</summary>
    Pending,

    /// <summary>Execute() has been entered and the script is actually running on the UI thread.</summary>
    Running,

    Completed,

    Error,

    /// <summary>The script observed the cancellation token and unwound cleanly (PRD §06) -- distinct from Error since the agent asked for this.</summary>
    Cancelled,

    /// <summary>Cancellation's grace period lapsed without the script actually stopping (PRD §06). Sticky until Revit restarts.</summary>
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
