using Rhino.MCPBridge.Core.Execution;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// Fire-and-forget onto the main thread; the dispatcher waits on the execution record, not on this.
/// RhinoApp.InvokeOnUiThread BLOCKS the calling thread until the action has run (found live: a
/// looping script made execute_script unable to answer `running` and the wire call timed out), so
/// the call is made from a pool thread, which is the one that blocks for the run's duration --
/// harmless, runs are serialised per instance anyway.
/// </summary>
internal sealed class RhinoRunLauncher : IRunLauncher
{
    private readonly Action<string> _log;
    public RhinoRunLauncher(Action<string> log) { _log = log; }

    public void Post(Action onMainThread) => Task.Run(() =>
    {
        try
        {
            RhinoApp.InvokeOnUiThread(() =>
            {
                try { onMainThread(); }
                catch (Exception ex) { _log("run failed outside the executor's own handling: " + ex); }
            });
        }
        catch (Exception ex)
        {
            _log("could not post the run to the main thread: " + ex);
        }
    });
}
