namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// The hand-off between <see cref="RhinoRunHost.RunInCommand"/> and the bridge's run command: the
/// host parks the body here, executes the command, and the command's RunCommand takes it. One slot,
/// main thread only, so no lock is needed and a stale body can never run under a later command.
/// </summary>
internal static class RunQueue
{
    private static Action? _pending;
    private static bool _ran;

    public static void Set(Action body) { _pending = body; _ran = false; }

    /// <summary>Called by the run command. Returns false when nothing was parked (someone typed the command).</summary>
    public static bool TakeAndRun()
    {
        var body = _pending;
        _pending = null;
        if (body is null) return false;
        _ran = true;
        body();
        return true;
    }

    public static bool Ran => _ran;
    public static void Clear() { _pending = null; }
}
