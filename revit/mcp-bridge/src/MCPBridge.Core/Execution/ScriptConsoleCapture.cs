using System;
using System.IO;
using System.Text;
using System.Threading;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Execution-scoped console capture (issue #52, resolving #35 properly). Installs a single
/// process-wide routing writer over <see cref="Console.Out"/> ONCE, on first use; per script run,
/// <see cref="Begin"/> sets an AsyncLocal ambient writer that flows with the run's execution context
/// -- so only that run's code (and whatever it awaits) is captured, while every other thread keeps
/// writing to the real console. This replaces a per-run process-global <c>Console.SetOut</c> swap
/// whose two failure modes were: any other thread's console writes leaking into a script's captured
/// stdout, and -- since xunit parallelizes test classes by default -- a latent cross-capture race
/// across the tier-1 suites.
///
/// The router is never uninstalled: it is behaviorally transparent when no ambient writer is set
/// (all writes forward to the original console), and uninstalling would reintroduce the ordering
/// races the once-only install exists to avoid. Known, accepted parity with the old design: a
/// script calling <c>Console.SetOut</c> itself replaces the router process-wide -- the same stomp it
/// could always do -- and that stays in the deliberate-interference bucket with reflection.
///
/// "Public means script-reachable" note: this type is internal, so a script cannot name it. A
/// script CAN observe the router instance via <c>Console.Out</c>, but the router carries no
/// capability beyond what <c>Console.Out</c> always carried -- it only forwards writes.
/// </summary>
internal static class ScriptConsoleCapture
{
    private static readonly AsyncLocal<TextWriter?> Ambient = new();
    private static TextWriter? _original;
    private static int _installed;

    /// <summary>
    /// Makes <paramref name="writer"/> the ambient capture target for the current execution context
    /// until the returned scope is disposed. Callers dispose in a finally (RunAsync's `using`), so
    /// an escaping exception can't leave a stale ambient writer behind.
    /// </summary>
    internal static IDisposable Begin(TextWriter writer)
    {
        EnsureInstalled();
        Ambient.Value = writer;
        return new Scope();
    }

    private static void EnsureInstalled()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
        {
            return;
        }

        _original = Console.Out;
        Console.SetOut(new RoutingWriter());
    }

    private static TextWriter Target => Ambient.Value ?? _original ?? TextWriter.Null;

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Ambient.Value = null;
    }

    private sealed class RoutingWriter : TextWriter
    {
        public override Encoding Encoding => Target.Encoding;

        public override void Write(char value) => Target.Write(value);

        public override void Write(char[] buffer, int index, int count) => Target.Write(buffer, index, count);

        public override void Write(string? value) => Target.Write(value);

        public override void WriteLine(string? value) => Target.WriteLine(value);

        public override void Flush() => Target.Flush();
    }
}
