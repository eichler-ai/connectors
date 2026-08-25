using System;
using Autodesk.Revit.UI;

namespace MCPBridge.RevitAdapter;

/// <summary>Real implementation wrapping Autodesk.Revit.UI.ExternalEvent.Raise(). Not unit-tested (see RevitTransactionAdapter).</summary>
public sealed class RevitExternalEventRaiser : IExternalEventRaiser
{
    private readonly ExternalEvent _externalEvent;

    public RevitExternalEventRaiser(ExternalEvent externalEvent)
    {
        _externalEvent = externalEvent;
    }

    public ExternalEventRaiseOutcome Raise() => _externalEvent.Raise() switch
    {
        ExternalEventRequest.Accepted => ExternalEventRaiseOutcome.Accepted,
        ExternalEventRequest.Denied => ExternalEventRaiseOutcome.Denied,
        ExternalEventRequest.TimedOut => ExternalEventRaiseOutcome.TimedOut,
        var other => throw new InvalidOperationException($"Unrecognized ExternalEventRequest value: {other}."),
    };
}
