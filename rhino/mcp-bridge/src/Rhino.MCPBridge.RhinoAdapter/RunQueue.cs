namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// The hand-off between <see cref="RhinoRunHost.RunInCommand"/> and the bridge's run command: the
/// host parks the body here, executes the command, and the command's RunCommand takes it. One slot,
/// main thread only, so no lock is needed and a stale body can never run under a later command.
/// </summary>
internal static class RunQueue
{
    private static Action? _pending;
    private static Action<string>? _log;
    private static bool _ran;

    public static void Set(Action body, Action<string> log) { _pending = body; _log = log; _ran = false; }

    /// <summary>Called by the run command. Returns false when nothing was parked (someone typed the
    /// command). Never throws: an exception here would unwind into Rhino's native command dispatcher
    /// (review of #282); the executor already turns failures into outcomes, so anything reaching this
    /// catch is a bridge bug, logged.</summary>
    public static bool TakeAndRun()
    {
        var body = _pending;
        _pending = null;
        if (body is null) return false;
        _ran = true;
        try { body(); }
        catch (Exception ex) { _log?.Invoke("run body threw past the executor: " + ex); }
        return true;
    }

    public static bool Ran => _ran;
    public static void Clear() { _pending = null; }
}
