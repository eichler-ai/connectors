using Autodesk.Revit.UI;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// The real IExternalEventHandler (PRD §06 step 2). Execute(UIApplication) is
/// Revit's callback on the UI thread once ExternalEvent.Raise() is honored by
/// the idle loop -- this is the pending -&gt; running transition. All it does is
/// wrap the live UIApplication in an adapter and forward to Core's callback;
/// no decision logic lives here. Not unit-tested (see RevitTransactionAdapter) --
/// Execute() firing at all requires a live Revit session.
///
/// INTERNAL, AND THE REASON IS WORTH READING BEFORE ANYONE WIDENS IT AGAIN. While this type was
/// public it was a live denylist bypass, found by the second independent review round of PR #25 --
/// one round AFTER the concrete adapters were made internal and the hole was believed closed.
/// Nothing in its signature exposes an adapter; the single line in Execute() does, by handing a real
/// RevitUiApplicationAdapter (as IUiApplicationAdapter) to a caller-supplied IScriptExecutionCallback.
/// A Roslyn script submission can declare types, so the script supplied that callback itself, captured
/// the adapter, cast it to IDocumentCreationSource and opened a real unmanaged Transaction on a
/// document it created -- without naming a single internal type. Note also that implementing
/// IExternalEventHandler.Execute explicitly/privately would NOT have fixed this: IExternalEventHandler
/// is a public Revit SDK interface a script can simply cast to. See IDocumentAdapter's doc comment for
/// the restated rule, and revit/test-harness/denylist_bypass_test.go's
/// TestConnectorCapabilitiesAreNotReachableThroughACallback, which pins it.
/// </summary>
internal sealed class RevitScriptExecutionHandler : IExternalEventHandler
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
