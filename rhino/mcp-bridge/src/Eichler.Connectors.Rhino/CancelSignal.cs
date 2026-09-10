using System.Threading;

namespace Eichler.Connectors.Rhino;

/// <summary>
/// The <c>cancel</c> global a Python script polls (rhino/docs/PRD.md §06). Cancellation of a Python
/// run is cooperative only -- Rhino's CPython host has no interrupt (spikes §1) -- so a long loop
/// calls <c>cancel.Check()</c> each iteration, which raises when the caller has cancelled the run and
/// lets the connector revert it; <c>cancel.IsRequested</c> is the non-raising form. A script that
/// never checks resolves to <c>unrecoverable</c> after the grace period.
/// </summary>
public sealed class CancelSignal
{
    private readonly CancellationToken _token;

    internal CancelSignal(CancellationToken token)
    {
        _token = token;
    }

    /// <summary>True once cancel_execution has been called for this run.</summary>
    public bool IsRequested => _token.IsCancellationRequested;

    /// <summary>Raises <see cref="System.OperationCanceledException"/> when cancellation has been requested; otherwise returns.</summary>
    public void Check() => _token.ThrowIfCancellationRequested();
}
