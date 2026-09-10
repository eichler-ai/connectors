namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// Posts work to Rhino's main thread without waiting for it (RhinoApp.InvokeOnUiThread in the real
/// adapter; inline in tests). The dispatcher waits on the execution record instead, so a run longer
/// than timeout_ms returns `running` and is polled -- never a blocked connection thread.
/// </summary>
internal interface IRunLauncher
{
    void Post(Action onMainThread);
}
