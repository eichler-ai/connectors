namespace MCPBridge.RevitAdapter;

/// <summary>
/// Thin seam over Autodesk.Revit.UI.ExternalEvent.Raise() (PRD §06). Core calls this
/// to wake Revit's idle loop; it never touches ExternalEvent itself.
/// </summary>
public interface IExternalEventRaiser
{
    /// <summary>
    /// Requests that Revit's idle loop invoke the paired IExternalEventHandler.Execute()
    /// soon. The outcome mirrors Autodesk.Revit.UI.ExternalEventRequest but is a
    /// RevitAdapter-owned type so Core (and its tests) can consume it without a live
    /// Revit dependency. Denied/TimedOut must never be silently discarded by a caller
    /// (PR #2 review, Fix 5) -- see <see cref="MCPBridge.Core.Execution.ExternalEventBridge{TResult}"/>,
    /// which turns a non-Accepted outcome into a failed Task rather than one that hangs
    /// forever.
    /// </summary>
    ExternalEventRaiseOutcome Raise();
}

/// <summary>
/// Mirrors Autodesk.Revit.UI.ExternalEventRequest (PRD §06), including all four real members. Pending
/// (second review finding) means "not accepted because the previous request on this same event is still
/// queued/not-yet-executed" -- it is not a failure. See <see cref="MCPBridge.Core.Execution.ExternalEventBridge{TResult}"/>,
/// which treats it the same as Accepted given this bridge's own usage pattern (a RunAsync call only ever
/// calls Raise() once, immediately after atomically setting its pending work item, so a Pending result
/// under this bridge means the request genuinely is still queued and Execute() will still eventually fire
/// for it).
/// </summary>
public enum ExternalEventRaiseOutcome
{
    Accepted,
    Pending,
    Denied,
    TimedOut,
}
