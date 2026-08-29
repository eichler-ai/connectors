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
/// stdout, and forcing the tier-1 suites to disable xunit's default class parallelism outright --
/// the assembly-level DisableTestParallelization attribute existed solely as that race's
/// mitigation, and was removed together with the swap it mitigated.
///
/// The router is never uninstalled: it is behaviorally transparent when no ambient writer is set
/// (all writes forward to the original console), and uninstalling would reintroduce ordering races.
/// A script calling <c>Console.SetOut</c> itself displaces the router; unlike the old per-run swap
/// (which self-healed on its finally), a once-only install would stay displaced forever, silently
/// reporting empty stdout for every later script -- so <see cref="Begin"/> re-installs when it
/// finds the router displaced (PR review). The stomp itself stays in the deliberate-interference
/// bucket with reflection. One documented capture nuance: the ambient writer FLOWS into threads and
/// tasks the script itself spawns (ExecutionContext semantics), so a spawned thread that outlives
/// its run keeps writing into that run's dead buffer -- silently swallowed, unbounded only by the
/// misbehaving script's own lifetime; scripts spawning unjoined threads are already outside the
/// supported model.
///
/// "Public means script-reachable" note: this type is internal, so a script cannot name it. A
/// script CAN observe the router instance via <c>Console.Out</c>, but the router carries no
/// capability beyond what <c>Console.Out</c> always carried -- it only forwards writes.
/// </summary>
internal static class ScriptConsoleCapture
{
    private static readonly AsyncLocal<TextWriter?> Ambient = new();
    private static readonly object InstallLock = new();
    private static TextWriter? _original;
    private static RoutingWriter? _router;

    /// <summary>
    /// Makes <paramref name="writer"/> the ambient capture target for the current execution context
    /// until the returned scope is disposed. Callers dispose in a finally (RunAsync's `using`), so
    /// an escaping exception can't leave a stale ambient writer behind.
    /// </summary>
    internal static IDisposable Begin(TextWriter writer)
    {
        EnsureInstalled();
        var scope = new Scope(Ambient.Value);
        Ambient.Value = writer;
        return scope;
    }

    private static void EnsureInstalled()
    {
        // A plain lock, not Interlocked (PR review): an exchange-based gate let the losing thread
        // proceed BEFORE the winner's SetOut landed, so its run's writes could reach the real
        // console -- production never races (ExternalEvent serializes runs) but parallel tests can.
        // Console.SetOut is checked each time so a script's own SetOut stomp is healed on the next
        // run rather than silently persisting (see the class doc).
        lock (InstallLock)
        {
            if (_router is not null && ReferenceEquals(Console.Out, _router.Installed))
            {
                return;
            }

            _original = Console.Out;
            _router = new RoutingWriter();
            Console.SetOut(_router);

            // Console.SetOut wraps the writer in TextWriter.Synchronized; remember the wrapper so
            // the displaced-router check above compares against what Console.Out actually returns.
            _router.Installed = Console.Out;
        }
    }

    private static TextWriter Target => Ambient.Value ?? _original ?? TextWriter.Null;

    private sealed class Scope : IDisposable
    {
        private readonly TextWriter? _prior;

        public Scope(TextWriter? prior) => _prior = prior;

        // Restores rather than nulls (PR review): nesting is impossible today (one script at a
        // time), but a restore is exactly as cheap and can't become wrong if that ever changes.
        public void Dispose() => Ambient.Value = _prior;
    }

    private sealed class RoutingWriter : TextWriter
    {
        /// <summary>What Console.Out returned after installing this router (its Synchronized wrapper) -- the displaced-router check's comparand.</summary>
        internal TextWriter? Installed { get; set; }

        public override Encoding Encoding => Target.Encoding;

        public override void Write(char value) => Target.Write(value);

        public override void Write(char[] buffer, int index, int count) => Target.Write(buffer, index, count);

        public override void Write(string? value) => Target.Write(value);

        public override void WriteLine(string? value) => Target.WriteLine(value);

        public override void Flush() => Target.Flush();
    }
}
