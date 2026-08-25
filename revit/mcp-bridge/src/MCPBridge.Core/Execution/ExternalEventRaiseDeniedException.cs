using System;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Thrown by <see cref="ExternalEventBridge{TResult}"/> when IExternalEventRaiser.Raise() comes back
/// anything other than Accepted (PR #2 review, Fix 5). Revit's ExternalEvent.Raise() previously had its
/// return value discarded entirely, so a Denied request (e.g. the event already has a raise queued) was
/// silently lost -- the caller's Task would simply never complete. Surfacing it as an exception on the
/// returned Task means an awaiting caller sees a clear, actionable failure instead of hanging.
/// </summary>
public sealed class ExternalEventRaiseDeniedException : Exception
{
    public const string Code = "external-event-raise-denied";

    public ExternalEventRaiseOutcome Outcome { get; }

    public ExternalEventRaiseDeniedException(ExternalEventRaiseOutcome outcome)
        : base(
            $"ExternalEvent.Raise() returned {outcome} instead of Accepted (code: {Code}); Revit's idle loop " +
            "did not accept the request to run this work item. This usually means another raise for the same " +
            "event was already queued.")
    {
        Outcome = outcome;
    }
}
