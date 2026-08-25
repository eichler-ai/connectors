using Autodesk.Revit.UI;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// The real IExternalEventHandler (PRD §06 step 2). Execute(UIApplication) is
/// Revit's callback on the UI thread once ExternalEvent.Raise() is honored by
/// the idle loop -- this is the pending -&gt; running transition. All it does is
/// wrap the live UIApplication in an adapter and forward to Core's callback;
/// no decision logic lives here. Not unit-tested (see RevitTransactionAdapter) --
/// Execute() firing at all requires a live Revit session.
/// </summary>
public sealed class RevitScriptExecutionHandler : IExternalEventHandler
{
    private readonly IScriptExecutionCallback _callback;
    private readonly string _name;

    public RevitScriptExecutionHandler(IScriptExecutionCallback callback, string name = "MCP Bridge script execution")
    {
        _callback = callback;
        _name = name;
    }

    public void Execute(UIApplication app)
    {
        _callback.OnExecute(new RevitUiApplicationAdapter(app));
    }

    public string GetName() => _name;
}
