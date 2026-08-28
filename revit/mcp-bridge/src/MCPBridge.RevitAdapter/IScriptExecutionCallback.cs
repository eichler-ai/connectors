namespace MCPBridge.RevitAdapter;

/// <summary>
/// Invoked by the real IExternalEventHandler.Execute(UIApplication) implementation
/// (see RevitScriptExecutionHandler) once Revit's idle loop actually enters it --
/// i.e. exactly the pending -&gt; running transition described in PRD §06.
/// Core implements this; RevitAdapter is only responsible for calling it on the
/// UI thread with real adapters wrapping the live UIApplication.
/// </summary>
internal interface IScriptExecutionCallback
{
    void OnExecute(IUiApplicationAdapter uiApplication);
}
