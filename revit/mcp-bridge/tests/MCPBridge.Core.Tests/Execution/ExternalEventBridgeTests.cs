using System;
using System.Threading.Tasks;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Tests.Fakes;
using MCPBridge.RevitAdapter;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

/// <summary>
/// PR #2 review, Fix 1 ("outer plumbing") and Fix 5 (Denied must not be discarded). Exercises
/// ExternalEventBridge&lt;TResult&gt; entirely through fakes -- IExternalEventRaiser and
/// IUiApplicationAdapter -- with OnExecute standing in for the real IExternalEventHandler.Execute()
/// callback, per the Core/RevitAdapter unit-test seam (no live Revit session needed).
/// </summary>
public class ExternalEventBridgeTests
{
    [Fact]
    public async Task RunAsync_RaiseAccepted_ThenOnExecuteFires_ResolvesTaskWithWorkResult()
    {
        var raiser = new FakeExternalEventRaiser { NextOutcome = ExternalEventRaiseOutcome.Accepted };
        var bridge = new ExternalEventBridge<int>(raiser);

        var task = bridge.RunAsync("exec-1", app => 42);
        Assert.False(task.IsCompleted); // must not block/complete before Execute() fires

        bridge.OnExecute(new FakeUiApplicationAdapter());

        var result = await task;
        Assert.Equal(42, result);
        Assert.Equal(1, raiser.RaiseCallCount);
    }

    [Fact]
    public async Task RunAsync_WorkThrows_TaskFaultsWithTheSameException_ExecuteItselfDoesNotThrow()
    {
        // Fix 1: Execute() (here, OnExecute) must never let an exception escape onto Revit's UI thread --
        // it must be captured and surfaced via the Task instead.
        var raiser = new FakeExternalEventRaiser();
        var bridge = new ExternalEventBridge<int>(raiser);

        var task = bridge.RunAsync("exec-1", app => throw new InvalidOperationException("boom"));

        var ex = Record.Exception(() => bridge.OnExecute(new FakeUiApplicationAdapter()));
        Assert.Null(ex); // OnExecute itself never throws

        var faulted = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal("boom", faulted.Message);
    }

    [Fact]
    public async Task RunAsync_RaiseDenied_FailsTaskImmediately_DoesNotWaitForExecute()
    {
        // Fix 5: ExternalEvent.Raise() returning Denied must never be silently discarded -- the caller's
        // Task must fail with a clear diagnostic instead of hanging forever.
        var raiser = new FakeExternalEventRaiser { NextOutcome = ExternalEventRaiseOutcome.Denied };
        var bridge = new ExternalEventBridge<int>(raiser);

        var task = bridge.RunAsync("exec-1", app => 42);

        var ex = await Assert.ThrowsAsync<ExternalEventRaiseDeniedException>(() => task);
        Assert.Equal(ExternalEventRaiseOutcome.Denied, ex.Outcome);
        Assert.Contains(ExternalEventRaiseDeniedException.Code, ex.Message);
    }

    [Fact]
    public async Task RunAsync_RaiseTimedOut_AlsoFailsTaskImmediately()
    {
        var raiser = new FakeExternalEventRaiser { NextOutcome = ExternalEventRaiseOutcome.TimedOut };
        var bridge = new ExternalEventBridge<int>(raiser);

        var task = bridge.RunAsync("exec-1", app => 42);

        var ex = await Assert.ThrowsAsync<ExternalEventRaiseDeniedException>(() => task);
        Assert.Equal(ExternalEventRaiseOutcome.TimedOut, ex.Outcome);
    }

    [Fact]
    public async Task RunAsync_RaisePending_IsNotAFailure_WorkItemStaysQueued_OnExecuteStillResolvesIt()
    {
        // Second review finding: Pending means "the previous request on this event is still queued", which
        // under this bridge's single-work-item-at-a-time usage pattern means the request this call just
        // queued genuinely is still queued and Execute() will still eventually fire for it -- not a failure.
        var raiser = new FakeExternalEventRaiser { NextOutcome = ExternalEventRaiseOutcome.Pending };
        var bridge = new ExternalEventBridge<int>(raiser);

        var task = bridge.RunAsync("exec-1", app => 42);
        Assert.False(task.IsCompleted);
        Assert.False(task.IsFaulted);

        bridge.OnExecute(new FakeUiApplicationAdapter());

        var result = await task;
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task RunAsync_RaiseDenied_DoesNotClobberADifferentWorkItem_QueuedByASubsequentCall()
    {
        // Compare-and-clear (second review finding): a failure from one RunAsync call must never null out
        // _pending if a different work item has since been queued there.
        var raiser = new FakeExternalEventRaiser { NextOutcome = ExternalEventRaiseOutcome.Denied };
        var bridge = new ExternalEventBridge<int>(raiser);

        var firstTask = bridge.RunAsync("exec-1", app => 1);
        await Assert.ThrowsAsync<ExternalEventRaiseDeniedException>(() => firstTask);

        // A second RunAsync after the first failed and cleared _pending should queue and resolve normally.
        raiser.NextOutcome = ExternalEventRaiseOutcome.Accepted;
        var secondTask = bridge.RunAsync("exec-2", app => 2);
        bridge.OnExecute(new FakeUiApplicationAdapter());

        Assert.Equal(2, await secondTask);
    }

    [Fact]
    public void OnExecute_WithNothingPending_DoesNotThrow()
    {
        // A spurious Execute() with no queued work (shouldn't happen given ExternalEvent's own one-shot
        // semantics, but Execute() must never throw regardless).
        var bridge = new ExternalEventBridge<int>(new FakeExternalEventRaiser());

        var ex = Record.Exception(() => bridge.OnExecute(new FakeUiApplicationAdapter()));

        Assert.Null(ex);
    }

    [Fact]
    public async Task OnExecute_PassesTheRealAdapter_ThroughToTheWorkDelegate()
    {
        var raiser = new FakeExternalEventRaiser();
        var bridge = new ExternalEventBridge<string>(raiser);
        var expectedAdapter = new FakeUiApplicationAdapter();

        var task = bridge.RunAsync("exec-1", app => ReferenceEquals(app, expectedAdapter) ? "same" : "different");
        bridge.OnExecute(expectedAdapter);

        Assert.Equal("same", await task);
    }

    [Fact]
    public async Task Abandon_WithPendingWorkItem_FaultsItAndClearsPending()
    {
        // Third review finding: a still-Pending execution that gets cancelled must not leave its queued
        // work item wedging the bridge forever -- Abandon() faults it instead.
        var raiser = new FakeExternalEventRaiser { NextOutcome = ExternalEventRaiseOutcome.Pending };
        var bridge = new ExternalEventBridge<int>(raiser);

        var task = bridge.RunAsync("exec-1", app => 42);
        Assert.False(task.IsCompleted);

        bridge.Abandon("exec-1");

        var ex = await Assert.ThrowsAsync<ExternalEventBridgeAbandonedException>(() => task);
        Assert.Contains(ExternalEventBridgeAbandonedException.Code, ex.Message);
    }

    [Fact]
    public void Abandon_WithNothingPending_DoesNotThrow()
    {
        var bridge = new ExternalEventBridge<int>(new FakeExternalEventRaiser());

        var ex = Record.Exception(() => bridge.Abandon("exec-1"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task Abandon_WrongExecutionId_DoesNotClobberTheActuallyPendingWorkItem()
    {
        // Second independent PR review finding: Abandon() must compare-and-clear on execution_id, exactly
        // like RunAsync's own Denied branch does -- a caller (BridgeHost's periodic timer, or
        // RequestDispatcher's cancel_execution handler) can be acting on a stale signal about a DIFFERENT
        // execution than whatever is actually queued in _pending by the time Abandon() runs. Calling
        // Abandon() with an id that doesn't match what's pending must be a no-op, not fault the wrong
        // work item.
        var raiser = new FakeExternalEventRaiser { NextOutcome = ExternalEventRaiseOutcome.Pending };
        var bridge = new ExternalEventBridge<int>(raiser);

        var task = bridge.RunAsync("exec-real", app => 42);
        Assert.False(task.IsCompleted);

        bridge.Abandon("exec-stale"); // a different, unrelated execution_id
        Assert.False(task.IsCompleted); // must NOT have been faulted

        bridge.OnExecute(new FakeUiApplicationAdapter());
        Assert.Equal(42, await task); // still resolves normally once Execute() actually fires
    }

    [Fact]
    public async Task Abandon_ThenOnExecuteStillFiresLater_IsANoOp_DoesNotDoubleResolve()
    {
        // Simulates the real scenario: Revit's idle loop still eventually enters Execute() for the
        // already-abandoned raise (nothing can un-queue it from Revit's side) -- OnExecute must not throw
        // or attempt to resolve a completion source that's already been faulted and forgotten.
        var raiser = new FakeExternalEventRaiser { NextOutcome = ExternalEventRaiseOutcome.Pending };
        var bridge = new ExternalEventBridge<int>(raiser);

        var task = bridge.RunAsync("exec-1", app => 42);
        bridge.Abandon("exec-1");
        await Assert.ThrowsAsync<ExternalEventBridgeAbandonedException>(() => task);

        var ex = Record.Exception(() => bridge.OnExecute(new FakeUiApplicationAdapter()));

        Assert.Null(ex);
    }

    [Fact]
    public async Task Abandon_DoesNotWedgeTheBridge_ASubsequentRunAsyncStillWorks()
    {
        var raiser = new FakeExternalEventRaiser { NextOutcome = ExternalEventRaiseOutcome.Pending };
        var bridge = new ExternalEventBridge<int>(raiser);

        var firstTask = bridge.RunAsync("exec-1", app => 1);
        bridge.Abandon("exec-1");
        await Assert.ThrowsAsync<ExternalEventBridgeAbandonedException>(() => firstTask);

        raiser.NextOutcome = ExternalEventRaiseOutcome.Accepted;
        var secondTask = bridge.RunAsync("exec-2", app => 2);
        bridge.OnExecute(new FakeUiApplicationAdapter());

        Assert.Equal(2, await secondTask);
    }
}
