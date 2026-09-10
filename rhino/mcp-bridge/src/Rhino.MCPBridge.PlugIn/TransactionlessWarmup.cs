using Rhino.MCPBridge.Core.Execution;
using Rhino.MCPBridge.Core.Execution.Python;

namespace Rhino.MCPBridge.PlugIn;

/// <summary>Runs the Roslyn warm-up compile and the Python language load on background threads once,
/// logging their outcomes. The Python load is off the main thread as the spikes verified (§1); every
/// script RUN stays on the main thread inside the run command.</summary>
internal sealed class TransactionlessWarmup
{
    private readonly RoslynScriptRunner _runner;
    private readonly IPythonHost _python;
    private readonly Action<string> _log;
    public TransactionlessWarmup(RoslynScriptRunner runner, IPythonHost python, Action<string> log) { _runner = runner; _python = python; _log = log; }

    public void Start()
    {
        new Thread(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { _runner.WarmupCompile(); _log($"roslyn warm-up done in {sw.ElapsedMilliseconds} ms (warm: {_runner.IsWarm})"); }
            catch (Exception ex) { _log("roslyn warm-up failed: " + ex.Message); }
        }) { IsBackground = true, Name = "MCPBridge roslyn warm-up" }.Start();
        new Thread(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _python.EnsureLoaded();
            _log(_python.UnavailableReason is { } why
                ? $"python warm-up failed after {sw.ElapsedMilliseconds} ms: {why}"
                : $"python warm-up done in {sw.ElapsedMilliseconds} ms");
        }) { IsBackground = true, Name = "MCPBridge python warm-up" }.Start();
    }
}
