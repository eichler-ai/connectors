using Rhino.MCPBridge.Core.Execution;

namespace Rhino.MCPBridge.PlugIn;

/// <summary>Runs the Roslyn warm-up compile on a background thread once, logging its outcome.</summary>
internal sealed class TransactionlessWarmup
{
    private readonly RoslynScriptRunner _runner;
    private readonly Action<string> _log;
    public TransactionlessWarmup(RoslynScriptRunner runner, Action<string> log) { _runner = runner; _log = log; }

    public void Start() => new Thread(() =>
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { _runner.WarmupCompile(); _log($"roslyn warm-up done in {sw.ElapsedMilliseconds} ms (warm: {_runner.IsWarm})"); }
        catch (Exception ex) { _log("roslyn warm-up failed: " + ex.Message); }
    }) { IsBackground = true, Name = "MCPBridge roslyn warm-up" }.Start();
}
