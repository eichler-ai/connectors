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

        var task = bridge.RunAsync(app => 42);
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

        var task = bridge.RunAsync(app => throw new InvalidOperationException("boom"));

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

        var task = bridge.RunAsync(app => 42);

        var ex = await Assert.ThrowsAsync<ExternalEventRaiseDeniedException>(() => task);
        Assert.Equal(ExternalEventRaiseOutcome.Denied, ex.Outcome);
        Assert.Contains(ExternalEventRaiseDeniedException.Code, ex.Message);
    }

    [Fact]
    public async Task RunAsync_RaiseTimedOut_AlsoFailsTaskImmediately()
    {
        var raiser = new FakeExternalEventRaiser { NextOutcome = ExternalEventRaiseOutcome.TimedOut };
        var bridge = new ExternalEventBridge<int>(raiser);

        var task = bridge.RunAsync(app => 42);

        var ex = await Assert.ThrowsAsync<ExternalEventRaiseDeniedException>(() => task);
        Assert.Equal(ExternalEventRaiseOutcome.TimedOut, ex.Outcome);
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

        var task = bridge.RunAsync(app => ReferenceEquals(app, expectedAdapter) ? "same" : "different");
        bridge.OnExecute(expectedAdapter);

        Assert.Equal("same", await task);
    }
}
