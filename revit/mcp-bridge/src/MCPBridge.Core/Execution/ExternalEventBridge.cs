using System;
using System.Threading.Tasks;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Hand-rolled bridge from Revit's synchronous, UI-thread-only IExternalEventHandler.Execute(UIApplication)
/// callback to a clean, awaitable Task&lt;TResult&gt; a background thread (the TCP-handling thread) can wait on
/// without blocking (PRD §06 / PR #2 review, Fix 1's "outer plumbing"). Deliberately not a dependency on
/// Revit.Async or RevitToolkit -- both were evaluated and rejected (Revit.Async looks unmaintained for
/// modern .NET; RevitToolkit's compatibility with net10.0-windows is unverified) -- this is small enough to
/// own directly: an IExternalEventRaiser, an IScriptExecutionCallback implementation (the real
/// IExternalEventHandler.Execute() forwards into this via RevitScriptExecutionHandler), and a
/// TaskCompletionSource to pass a work item in and get a Task out.
///
/// Execute() itself must stay fully synchronous end-to-end (Fix 1's confirmed architecture decision -- no
/// async/Task-based variant of IExternalEventHandler.Execute exists). <see cref="OnExecute"/> runs the
/// pending work item and resolves its Task synchronously, before returning -- it never awaits anything.
/// Any blocking a work item needs to do (e.g. draining a Roslyn script's Task via
/// <c>.GetAwaiter().GetResult()</c>) happens inside the work delegate itself, and is deadlock-safe there
/// specifically because scripts are rejected before compilation if they contain their own top-level `await`
/// (see RoslynScriptRunner) -- verified against dotnet/roslyn#6928: a Roslyn script with no internal await
/// executes synchronously to completion before its Task is even returned, so there is no pending
/// continuation to deadlock on.
///
/// Only one work item is in flight at a time, matching ExecutionManager's own single-active-execution
/// invariant (PRD §06: Revit's UI thread runs one script at a time).
/// </summary>
public sealed class ExternalEventBridge<TResult> : IScriptExecutionCallback
{
    private readonly IExternalEventRaiser _raiser;
    private readonly object _lock = new();
    private PendingWork? _pending;

    public ExternalEventBridge(IExternalEventRaiser raiser)
    {
        _raiser = raiser;
    }

    /// <summary>
    /// Queues <paramref name="work"/> to run on Revit's UI thread the next time Execute() fires, raises the
    /// external event, and returns a Task that resolves once that happens. Never blocks the calling thread.
    /// If the raise itself is denied or times out (Fix 5), the returned Task fails immediately with
    /// <see cref="ExternalEventRaiseDeniedException"/> rather than hanging forever. A Pending outcome
    /// (second review finding) is NOT a failure -- given this bridge's single-work-item-at-a-time usage
    /// pattern, it means the request genuinely is still queued in Revit and Execute() will still eventually
    /// fire for it, so it's treated the same as Accepted: the work item stays queued and this call simply
    /// returns the still-pending Task.
    /// </summary>
    public Task<TResult> RunAsync(Func<IUiApplicationAdapter, TResult> work)
    {
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock)
        {
            if (_pending is not null)
            {
                // ExecutionManager's single-active-execution invariant (PRD §06) should make this
                // unreachable in practice -- a second execute_script while one is active gets Busy before a
                // second RunAsync is ever attempted -- but fail loudly rather than silently drop or
                // overwrite a still-pending work item if it ever happens.
                tcs.TrySetException(new InvalidOperationException(
                    "ExternalEventBridge already has a work item pending; RunAsync must not be called again " +
                    "before the previous one completes."));
                return tcs.Task;
            }

            _pending = new PendingWork(work, tcs);
        }

        var outcome = _raiser.Raise();
        if (outcome is ExternalEventRaiseOutcome.Denied or ExternalEventRaiseOutcome.TimedOut)
        {
            // Compare-and-clear rather than an unconditional null-out: verify _pending is still exactly
            // this call's work item (same TaskCompletionSource) before nulling it out. In this bridge's
            // current usage a concurrent OnExecute() can't have consumed _pending in between (that only
            // happens after a successful/Pending Raise(), which this branch by definition didn't get), so
            // this is defensive rather than load-bearing today -- but it means a failure from this call can
            // never clobber a different work item a subsequent call may have already queued.
            lock (_lock)
            {
                if (_pending is { } current && ReferenceEquals(current.CompletionSource, tcs))
                {
                    _pending = null;
                }
            }

            tcs.TrySetException(new ExternalEventRaiseDeniedException(outcome));
        }

        // Accepted and Pending both leave _pending queued as-is; OnExecute() will eventually consume it.
        return tcs.Task;
    }

    /// <summary>
    /// The IScriptExecutionCallback entry point (wired via RevitScriptExecutionHandler): called
    /// synchronously by the real IExternalEventHandler.Execute(UIApplication) once Revit's idle loop
    /// actually enters it. Runs the pending work item and resolves its Task before returning; never awaits
    /// anything itself (Fix 1) -- any blocking the work item needs happens inside it, synchronously.
    /// </summary>
    public void OnExecute(IUiApplicationAdapter uiApplication)
    {
        PendingWork? pending;
        lock (_lock)
        {
            pending = _pending;
            _pending = null;
        }

        if (pending is null)
        {
            // A spurious Execute() with nothing queued shouldn't happen given ExternalEvent's own one-shot
            // semantics, but there is nothing safe to do about it here beyond not throwing on Revit's UI
            // thread, so just return.
            return;
        }

        try
        {
            var result = pending.Value.Work(uiApplication);
            pending.Value.CompletionSource.TrySetResult(result);
        }
        catch (Exception ex)
        {
            pending.Value.CompletionSource.TrySetException(ex);
        }
    }

    private readonly record struct PendingWork(Func<IUiApplicationAdapter, TResult> Work, TaskCompletionSource<TResult> CompletionSource);
}
