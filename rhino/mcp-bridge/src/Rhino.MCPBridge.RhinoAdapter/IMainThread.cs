namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>Runs work on Rhino's main thread and waits for it (PRD §06: RhinoCommon is main-thread
/// only, and the CPython host does not marshal for us — spikes §1). Faked in tier 1 by running inline.</summary>
internal interface IMainThread
{
    T Invoke<T>(Func<T> func);
    void Invoke(Action action);
}
