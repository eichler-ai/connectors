using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Runs one undo or redo against a live Revit session (#146 Phase 2c) and reports what it reverted.
///
/// THE SHAPE FOLLOWS FROM TWO REVIT FACTS. (1) <c>PostCommand</c> queues the command to run after the
/// posting API context returns, so the UI-thread work item can only SUBSCRIBE and POST; the effect
/// arrives later as a <c>DocumentChanged</c> event on the UI thread. (2) Revit API calls -- including
/// unsubscribing -- are only legal on the UI thread, so the connection thread that is waiting must never
/// touch the subscription itself. Hence: the work item subscribes and posts; the first matching event
/// completes a TaskCompletionSource AND disposes the subscription (both on the UI thread); the waiting
/// thread only awaits the task with a timeout. On timeout the handler is left armed but DISARMED
/// logically (an interlocked flag, so exactly one of {handler completes, waiter disarms} wins) and
/// disposes itself on whatever event arrives next -- the cost of never calling Revit off-thread is one
/// dormant handler until the next document change.
///
/// (3) A THIRD FACT, FOUND LIVE: a posted command executes only when Revit's message loop WAKES. With
/// the connector merely waiting, nothing woke it -- the undo landed ~150ms after the deadline, every
/// time, on the next tool call's ExternalEvent. So while waiting, this raises no-op bridge work items
/// (each Raise() posts a message) until the event lands.
///
/// WHAT COUNTS AS THE EFFECT: the FIRST event whose operation is Undone (for undo) or Redone (for
/// redo). This assumes Revit raises one DocumentChanged per undo step, which is what every live run has
/// shown for a group assimilated by the connector (its transactions collapse to one undo step). A
/// multi-document group -- one run writing to several documents -- is NOT covered by that assumption:
/// if Revit raised one event per document, only the first would be reported. Untested; stated rather
/// than assumed away.
/// </summary>
internal sealed class UndoRedoCoordinator
{
    /// <summary>Longest a caller may wait for the posted command to take effect; Revit runs it on the next idle, so seconds is generous.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    /// <summary>Shortest wait honoured: below this the answer would be "not observed" before Revit has had a chance to run the command at all.</summary>
    public static readonly TimeSpan MinWait = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);

    /// <summary>How often Revit's loop is nudged while waiting -- see the class comment, fact (3).</summary>
    public static readonly TimeSpan NudgeInterval = TimeSpan.FromMilliseconds(250);

    private readonly ExternalEventBridge<ScriptExecutionOutcome> _bridge;
    private readonly Action<string>? _trace;

    /// <param name="trace">Connection-log sink for the timeline (subscribe, post, each event seen, outcome) -- the only way to diagnose a missed event after the fact.</param>
    public UndoRedoCoordinator(ExternalEventBridge<ScriptExecutionOutcome> bridge, Action<string>? trace = null)
    {
        _bridge = bridge;
        _trace = trace;
    }

    /// <summary>
    /// Posts <paramref name="direction"/> and waits up to <paramref name="wait"/> for the event it raises.
    /// Never throws for a Revit-side refusal or a timeout -- those are outcomes the caller reports.
    /// <paramref name="expectedDocumentId"/>, when given, is compared against the ACTIVE document inside
    /// the work item, before posting: Revit's undo acts on the active document's stack, and a person can
    /// change the active document between the agent's script and the agent's undo. This is the one guard
    /// that fires BEFORE anything changes; the transaction names arrive only after.
    /// </summary>
    public async Task<UndoRedoOutcome> RunAsync(UndoRedoDirection direction, TimeSpan wait, string operationTag, string? expectedDocumentId = null)
    {
        var observed = new TaskCompletionSource<DocumentChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wanted = direction == UndoRedoDirection.Undo ? DocumentChangeOperation.Undone : DocumentChangeOperation.Redone;
        var listener = new ListenerState();

        try
        {
            await _bridge.RunAsync(operationTag, uiApplication =>
            {
                if (uiApplication is not IDocumentChangeSource changes || uiApplication is not IPostableCommandSource commands)
                {
                    throw new NotSupportedException(
                        $"{direction} needs a live Revit session: {uiApplication.GetType().Name} implements neither " +
                        $"{nameof(IDocumentChangeSource)} nor {nameof(IPostableCommandSource)}, which only the live adapter does.");
                }

                if (expectedDocumentId is not null)
                {
                    var active = uiApplication.ActiveUiDocument?.Document;
                    var activeId = active?.DocumentId;
                    if (activeId != expectedDocumentId)
                    {
                        throw new UndoRedoDocumentMismatchException(expectedDocumentId, activeId, active?.Title);
                    }
                }

                // Subscribe BEFORE posting: the command runs after this work item returns, and the event it
                // raises must find a listener. The handler completes the task and unsubscribes itself, both
                // on the UI thread -- the only thread allowed to. (The `+=` cannot re-enter on this thread,
                // so `subscription` is always assigned before the handler can run.)
                IDisposable? subscription = null;
                subscription = changes.Subscribe(change =>
                {
                    _trace?.Invoke($"[{operationTag}] DocumentChanged op={change.OperationName} doc={change.DocumentId} " +
                        $"added={change.Added.Count} modified={change.Modified.Count} deleted={change.Deleted.Count} " +
                        $"names=[{string.Join("|", change.TransactionNames)}] armed={listener.IsArmed}");
                    if (!listener.IsArmed)
                    {
                        subscription?.Dispose();
                        return;
                    }

                    if (change.Operation != wanted)
                    {
                        return;
                    }

                    // Exactly one side wins the disarm; if the waiter got there first, this event belongs to
                    // no one and the listener simply goes away.
                    subscription?.Dispose();
                    if (listener.TryDisarm())
                    {
                        observed.TrySetResult(change);
                    }
                });
                _trace?.Invoke($"[{operationTag}] subscribed; posting {direction}");

                try
                {
                    if (direction == UndoRedoDirection.Undo)
                    {
                        commands.PostUndo();
                    }
                    else
                    {
                        commands.PostRedo();
                    }

                    _trace?.Invoke($"[{operationTag}] posted");
                }
                catch
                {
                    // Nothing will ever fire for a command that was not posted: release the listener now
                    // (still on the UI thread) rather than leaving it dormant.
                    listener.TryDisarm();
                    subscription.Dispose();
                    throw;
                }

                return ScriptExecutionOutcome.Completed(null, "");
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return UndoRedoOutcome.Refused(ex);
        }

        var deadline = DateTime.UtcNow + wait;
        var nudgeIndex = 0;
        while (!observed.Task.IsCompleted && DateTime.UtcNow < deadline)
        {
            // Nudge, then wait a slice for either the event or the nudge to have run. A nudge that could
            // not be raised is a FAULTED task (the bridge never throws from RunAsync): the one realistic
            // cause is another work item already pending -- a script that slipped past the busy gate --
            // and that is worth a trace line, not silence.
            var nudgeTag = $"{operationTag}-nudge{nudgeIndex++}";
            var nudge = _bridge.RunAsync(nudgeTag, _ => ScriptExecutionOutcome.Completed(null, ""));

            var remaining = deadline - DateTime.UtcNow;
            var slice = remaining < NudgeInterval ? remaining : NudgeInterval;
            if (slice > TimeSpan.Zero)
            {
                await Task.WhenAny(observed.Task, Task.Delay(slice)).ConfigureAwait(false);
            }

            if (nudge.IsFaulted)
            {
                _trace?.Invoke($"[{operationTag}] nudge could not be raised: {nudge.Exception?.GetBaseException().Message}");
                _ = nudge.Exception; // observed
            }
            else if (!nudge.IsCompleted)
            {
                // Still queued (Revit has not woken yet, or the event already landed): abandon it so the
                // bridge is free for the next request; when it eventually runs it is a no-op either way.
                _bridge.Abandon(nudgeTag);
            }
        }

        // Exactly one of the two sides disarms. If the waiter wins, an event arriving later is ignored and
        // releases the listener; if the handler won first (even a moment ago), its result is authoritative.
        if (!listener.TryDisarm())
        {
            return UndoRedoOutcome.Observed(await observed.Task.ConfigureAwait(false));
        }

        _trace?.Invoke($"[{operationTag}] no {wanted} event within {wait.TotalMilliseconds}ms; listener disarmed");
        return UndoRedoOutcome.NotObserved(wait);
    }
}

/// <summary>
/// The arm/disarm handshake between the UI-thread handler and the waiting thread. Interlocked, not a
/// bare flag: a check-then-act on a volatile bool let the handler complete the task in the window between
/// the waiter's check and its disarm, so an undo that HAD happened and WAS seen could still be reported
/// as not observed (independent review of #156).
/// </summary>
internal sealed class ListenerState
{
    private int _armed = 1;

    public bool IsArmed => Volatile.Read(ref _armed) == 1;

    /// <summary>True for exactly one caller.</summary>
    public bool TryDisarm() => Interlocked.CompareExchange(ref _armed, 0, 1) == 1;
}

internal enum UndoRedoDirection
{
    Undo,
    Redo,
}

/// <summary>Refusal raised INSIDE the work item, before posting, when the active document is not the one the caller expected.</summary>
internal sealed class UndoRedoDocumentMismatchException : InvalidOperationException
{
    public UndoRedoDocumentMismatchException(string expectedDocumentId, string? activeDocumentId, string? activeTitle)
        : base($"the active document is {(activeDocumentId is null ? "none" : $"'{activeTitle}' ({activeDocumentId})")}, not {expectedDocumentId}; " +
               "Revit's undo acts on the active document's stack, so nothing was posted.")
    {
        ExpectedDocumentId = expectedDocumentId;
        ActiveDocumentId = activeDocumentId;
    }

    public string ExpectedDocumentId { get; }

    public string? ActiveDocumentId { get; }
}

/// <summary>One undo/redo attempt's result: what Revit reverted, or why nothing was observed.</summary>
internal sealed class UndoRedoOutcome
{
    private UndoRedoOutcome(DocumentChange? change, Exception? refusal, TimeSpan? waited)
    {
        Change = change;
        Refusal = refusal;
        Waited = waited;
    }

    /// <summary>The event the command raised, when one was observed.</summary>
    public DocumentChange? Change { get; }

    /// <summary>Set when the command could not even be posted (Revit refused, wrong active document, or no live adapter).</summary>
    public Exception? Refusal { get; }

    /// <summary>Set when the command was posted but no matching event arrived within this wait.</summary>
    public TimeSpan? Waited { get; }

    public static UndoRedoOutcome Observed(DocumentChange change) => new(change, null, null);

    public static UndoRedoOutcome Refused(Exception refusal) => new(null, refusal, null);

    public static UndoRedoOutcome NotObserved(TimeSpan waited) => new(null, null, waited);

    /// <summary>
    /// The reverted delta in the mutation report's terms. An UNDO's event lists what the undo did:
    /// elements it removed appear as deleted, elements it brought back as added -- so the report reads
    /// as "what changed in the model by this undo", the same convention as a script run's report.
    /// </summary>
    public MutationReport? RevertedDelta()
    {
        if (Change is null)
        {
            return null;
        }

        var tracker = new MutationTracker();
        tracker.Record(Change);
        return tracker.Build();
    }

    /// <summary>The names of the transactions the command reverted -- "MCP: …" for a connector run, anything else for a person's action.</summary>
    public IReadOnlyList<string> RevertedTransactionNames => Change?.TransactionNames ?? Array.Empty<string>();

    /// <summary>
    /// True when every reverted transaction carries one of the connector's own names. A person or another
    /// add-in could in principle name a transaction "MCP: …" and be misread as connector work; accepted --
    /// the notice still prints the names, so the reader can judge.
    /// </summary>
    public bool RevertedOnlyConnectorWork =>
        RevertedTransactionNames.Count > 0 && RevertedTransactionNames.All(n => n.StartsWith(UndoLabel.Prefix, StringComparison.Ordinal) || n == UndoLabel.Default);
}
