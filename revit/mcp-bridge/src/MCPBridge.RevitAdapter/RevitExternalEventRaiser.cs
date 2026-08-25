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

    public void Raise() => _externalEvent.Raise();
}
