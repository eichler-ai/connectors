using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Tests.Fakes;

/// <summary>Test double for IExternalEventRaiser -- lets tests drive the outcome ExternalEvent.Raise() reports without a live Revit session.</summary>
public sealed class FakeExternalEventRaiser : IExternalEventRaiser
{
    public ExternalEventRaiseOutcome NextOutcome { get; set; } = ExternalEventRaiseOutcome.Accepted;

    public int RaiseCallCount { get; private set; }

    public ExternalEventRaiseOutcome Raise()
    {
        RaiseCallCount++;
        return NextOutcome;
    }
}
