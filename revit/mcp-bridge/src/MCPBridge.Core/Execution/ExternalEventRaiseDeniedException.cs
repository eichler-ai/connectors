using System;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Thrown by <see cref="ExternalEventBridge{TResult}"/> when IExternalEventRaiser.Raise() comes back
/// Denied or TimedOut (PR #2 review, Fix 5) -- genuine failures where the request will never run, unlike
/// Pending (a real fourth ExternalEventRequest member added in the second review pass), which just means
/// the request is still queued and Execute() will still eventually fire for it, so it's not treated as a
/// failure at all. Revit's ExternalEvent.Raise() previously had its return value discarded entirely, so a
/// Denied/TimedOut request was silently lost -- the caller's Task would simply never complete. Surfacing it
/// as an exception on the returned Task means an awaiting caller sees a clear, actionable failure instead
/// of hanging.
/// </summary>
public sealed class ExternalEventRaiseDeniedException : Exception
{
    public const string Code = "external-event-raise-denied";

    public ExternalEventRaiseOutcome Outcome { get; }

    public ExternalEventRaiseDeniedException(ExternalEventRaiseOutcome outcome)
        : base(
            $"ExternalEvent.Raise() returned {outcome} (code: {Code}); Revit's idle loop rejected the request " +
            "to run this work item outright rather than merely queueing it. Denied usually means the paired " +
            "IExternalEventHandler itself failed; TimedOut usually means a thread-synchronization issue.")
    {
        Outcome = outcome;
    }
}
