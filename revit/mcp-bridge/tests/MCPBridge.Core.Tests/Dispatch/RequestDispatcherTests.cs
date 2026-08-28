using System;
using System.Text.Json;
using System.Threading.Tasks;
using MCPBridge.Core.Diagnostics;
using MCPBridge.Core.Dispatch;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Protocol;
using MCPBridge.Core.Tests.Fakes;
using MCPBridge.RevitAdapter;
using Xunit;

namespace MCPBridge.Core.Tests.Dispatch;

/// <summary>
/// Exercises RequestDispatcher's routing logic entirely through fakes (FakeExternalEventRaiser,
/// FakeUiApplicationAdapter/FakeUiDocumentAdapter/FakeDocumentAdapter) -- no real socket, no live Revit
/// session, following the same Core/RevitAdapter unit-test seam ExternalEventBridgeTests already uses.
///
/// Timing note: ExternalEventBridge{TResult}.RunAsync sets its pending work item and calls Raise()
/// synchronously, and HandleExecuteScriptAsync's own code runs synchronously up to its first `await`
/// (Task.WhenAny), which happens strictly after that RunAsync call -- so by the time
/// dispatcher.DispatchAsync(...) returns a Task to the test, the work item is already queued on the
/// bridge instance the test itself constructed and passed in. That lets these tests drive OnExecute()
/// deterministically (no timing races, no sleeps) exactly like ExternalEventBridgeTests does.
/// </summary>
public class RequestDispatcherTests
{
    private static ExecutionManager NewExecutionManager() =>
        new(new ExecutionRingBuffer(capacity: 50, retention: TimeSpan.FromMinutes(10)), gracePeriod: TimeSpan.FromSeconds(5));

    private static TransactionScriptExecutor NewScriptExecutor() => new(new RoslynScriptRunner(additionalMetadataReferencePaths: RevitApiReference.Paths));

    private static JsonRpcRequest ExecuteScriptRequest(int id, string executionId, string script, long timeoutMs = 30_000, long maxDurationMs = 600_000) =>
        Parse(new
        {
            jsonrpc = "2.0",
            id,
            method = "execute_script",
            @params = new { execution_id = executionId, document_id = "doc-1", script, timeout_ms = timeoutMs, max_duration_ms = maxDurationMs },
        });

    private static JsonRpcRequest PollRequest(int id, string executionId, long timeoutMs = 30_000) =>
        Parse(new { jsonrpc = "2.0", id, method = "poll_execution", @params = new { execution_id = executionId, timeout_ms = timeoutMs } });

    private static JsonRpcRequest CancelRequest(int id, string executionId) =>
        Parse(new { jsonrpc = "2.0", id, method = "cancel_execution", @params = new { execution_id = executionId } });

    private static JsonRpcRequest Parse(object envelope) => JsonRpcRequest.Parse(JsonSerializer.Serialize(envelope));

    private static FakeUiApplicationAdapter NewUiApp(FakeDocumentAdapter? document = null) =>
        new() { ActiveUiDocument = new FakeUiDocumentAdapter { Document = document ?? new FakeDocumentAdapter() } };

    [Fact]
    public async Task ExecuteScript_Success_ReturnsSuccessResultWithOutput()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var dispatchTask = dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", "1 + 1"));
        bridge.OnExecute(NewUiApp());

        var json = await dispatchTask;

        Assert.Contains("\"status\":\"success\"", json);
        Assert.Contains("\"execution_id\":\"exec-1\"", json);
        Assert.Contains("\"output\":\"2\"", json);
    }

    [Fact]
    public async Task ExecuteScript_ScriptThrows_ReturnsErrorResult()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var dispatchTask = dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", "throw new System.InvalidOperationException(\"boom\");"));
        bridge.OnExecute(NewUiApp());

        var json = await dispatchTask;

        Assert.Contains("\"status\":\"error\"", json);
        Assert.Contains("\"code\":\"script-execution-failed\"", json);
    }

    [Fact]
    public async Task ExecuteScript_NoActiveDocument_ReturnsErrorWithoutTouchingScriptExecutor()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var dispatchTask = dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", "1 + 1"));
        bridge.OnExecute(new FakeUiApplicationAdapter()); // ActiveUiDocument is null

        var json = await dispatchTask;

        Assert.Contains("\"status\":\"error\"", json);
        Assert.Contains("\"code\":\"no-active-document\"", json);
    }

    [Fact]
    public async Task ExecuteScript_WhileAnotherIsActive_ReturnsBusy_PointingAtTheActiveOne_NeverRaisesForTheSecond()
    {
        var executionManager = NewExecutionManager();
        var raiser = new FakeExternalEventRaiser();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(raiser);
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var firstDispatch = dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", "1 + 1")); // still Pending, work item queued

        var secondJson = await dispatcher.DispatchAsync(ExecuteScriptRequest(2, "exec-2", "2 + 2"));

        Assert.Contains("\"status\":\"busy\"", secondJson);
        Assert.Contains("\"execution_id\":\"exec-1\"", secondJson);
        Assert.Equal(1, raiser.RaiseCallCount); // exec-2 never reached RunAsync/Raise() at all

        bridge.OnExecute(NewUiApp());
        await firstDispatch; // drain so nothing's left dangling for the test process
    }

    [Fact]
    public async Task ExecuteScript_EmptyExecutionId_ReturnsJsonRpcError_NotAnException()
    {
        // params-validation path (JsonRpcRequest.GetRequiredString) -- a malformed execution_id never
        // even reaches ExecutionManager.Start.
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var json = await dispatcher.DispatchAsync(ExecuteScriptRequest(1, "", "1 + 1"));

        Assert.Contains("\"error\":{", json);
        Assert.Contains("-32602", json);
    }

    [Fact]
    public async Task ExecuteScript_ExecutionIdCollidesWithATerminalRecord_ReturnsJsonRpcError_NotAnException()
    {
        // Hard requirement 4: ExecutionManager.Start's ArgumentException (a broker-sourced executionId
        // that collides with an existing, already-terminal ring-buffer entry) must become a JSON-RPC
        // error response, not propagate and kill the connection.
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var firstDispatch = dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", "1 + 1"));
        bridge.OnExecute(NewUiApp());
        await firstDispatch; // exec-1 is now terminal (Completed)

        var json = await dispatcher.DispatchAsync(ExecuteScriptRequest(2, "exec-1", "2 + 2"));

        Assert.Contains("\"error\":{", json);
        Assert.Contains("-32602", json);
    }

    [Fact]
    public async Task ExecuteScript_RaiseDenied_CompletesAsError_AndDoesNotLeaveTheInstanceDangling()
    {
        // Hard requirement 2: a fault on the bridge's Task (ExternalEventRaiseDeniedException, here) must
        // call CompleteError so ExecutionManager's active slot doesn't dangle forever.
        var executionManager = NewExecutionManager();
        var raiser = new FakeExternalEventRaiser { NextOutcome = ExternalEventRaiseOutcome.Denied };
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(raiser);
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var json = await dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", "1 + 1"));

        Assert.Contains("\"status\":\"error\"", json);
        Assert.Contains("\"code\":\"execution-bridge-fault\"", json);

        // A second, different execution must be able to proceed normally -- the instance is not stuck Busy.
        raiser.NextOutcome = ExternalEventRaiseOutcome.Accepted;
        var secondDispatch = dispatcher.DispatchAsync(ExecuteScriptRequest(2, "exec-2", "1 + 1"));
        bridge.OnExecute(NewUiApp());
        var secondJson = await secondDispatch;

        Assert.Contains("\"status\":\"success\"", secondJson);
    }

    [Fact]
    public async Task ExecuteScript_CancelledWhileStillPending_ThenBridgeAbandoned_NeverRunsTheWorkItemAtAll()
    {
        // Hard requirement 3 (the common case in practice): cancel_execution's Abandon() call faults the
        // queued work item's Task directly and clears ExternalEventBridge._pending -- so by the time
        // Revit's idle loop actually gets around to firing Execute() for the already-raised event,
        // OnExecute finds nothing queued and no-ops. RunScriptWorkItem's own cancellation check (hard
        // requirement 1) never even runs in THIS race -- Abandon() already fully closed it. Verified here:
        // OnExecute after Abandon() must be a safe no-op, and the dispatch must have already resolved to
        // Cancelled from the cancel_execution call itself, not from OnExecute ever firing.
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());
        var document = new FakeDocumentAdapter();

        var dispatchTask = dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", "1 + 1")); // Pending, queued

        var cancelJson = await dispatcher.DispatchAsync(CancelRequest(2, "exec-1"));
        Assert.Contains("\"status\":\"cancelled\"", cancelJson);

        // Simulates Revit's idle loop eventually firing Execute() for the already-raised (but now
        // Abandon()-cleared) event -- must be a harmless no-op, not a second resolution attempt.
        bridge.OnExecute(NewUiApp(document));
        var executeJson = await dispatchTask;

        Assert.Contains("\"status\":\"cancelled\"", executeJson);
        Assert.Null(document.LastTransaction);
        Assert.Null(document.LastTransactionGroup);
    }

    [Fact]
    public async Task RunScriptWorkItem_CancellationTokenAlreadySet_BailsOutWithoutTouchingTheModel()
    {
        // Hard requirement 1's ACTUAL code path (RequestDispatcherTests' prior version of this test only
        // exercised hard requirement 3's Abandon() no-op -- Abandon() had already cleared _pending before
        // OnExecute ever ran, so RunScriptWorkItem's own cancellation check never executed; see that test's
        // updated name/comment above). This test drives the narrower, genuinely-still-reachable race
        // directly: ExecutionManager.RequestCancellation runs WITHOUT going through the dispatcher's
        // cancel_execution (so Abandon() is never called and the bridge's queued work item is untouched) --
        // simulating the exact window between RequestCancellation's lock release and Abandon() being called
        // in the real cancel_execution handler, where Revit's idle loop could still fire Execute() for the
        // still-queued raise before Abandon() gets to it. RunScriptWorkItem's own token check must catch
        // this and bail out without ever starting a transaction.
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());
        var document = new FakeDocumentAdapter();

        var dispatchTask = dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", "1 + 1")); // Pending, queued
        executionManager.RequestCancellation("exec-1", DateTimeOffset.UtcNow); // bypasses Abandon() deliberately

        bridge.OnExecute(NewUiApp(document)); // RunScriptWorkItem actually runs now; its token check must fire
        var executeJson = await dispatchTask;

        Assert.Contains("\"status\":\"cancelled\"", executeJson);
        Assert.Null(document.LastTransaction);
        Assert.Null(document.LastTransactionGroup);
    }

    [Fact]
    public async Task CancelExecution_UnknownId_ReturnsJsonRpcError()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var json = await dispatcher.DispatchAsync(CancelRequest(1, "exec-unknown"));

        Assert.Contains("\"error\":{", json);
        Assert.Contains("unknown_execution_id", json);
    }

    [Fact]
    public async Task CancelExecution_OnARunningExecution_StaysRunning_NotImmediatelyCancelled()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        // A script that's already Running (not Pending) by the time cancel_execution arrives: use a
        // long-running-in-spirit script; since we don't actually block, just confirm MarkRunning already
        // happened via a real execute run whose result we haven't polled for cancellation timing --
        // simplest deterministic way here is to run the script to completion first, then attempt to cancel
        // an execution manually put into Running via ExecutionManager directly.
        var now = DateTimeOffset.UtcNow;
        executionManager.Start("exec-1", "1 + 1", 600_000, now);
        executionManager.MarkRunning("exec-1", now);

        var json = await dispatcher.DispatchAsync(CancelRequest(1, "exec-1"));

        Assert.Contains("\"status\":\"running\"", json);
    }

    [Fact]
    public async Task PollExecution_UnknownId_ReturnsJsonRpcError()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var json = await dispatcher.DispatchAsync(PollRequest(1, "exec-unknown"));

        Assert.Contains("\"error\":{", json);
        Assert.Contains("unknown_execution_id", json);
    }

    [Fact]
    public async Task PollExecution_KnownPendingId_ReturnsPendingStatus()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        executionManager.Start("exec-1", "1 + 1", 600_000, DateTimeOffset.UtcNow);

        // Second live-wiring review finding: poll_execution now genuinely waits up to timeout_ms for a
        // terminal state (see HandlePollExecutionAsync) -- a short timeout here keeps this test fast while
        // still exercising the real wait-then-answer-with-current-status path (nothing ever marks exec-1
        // Running/terminal in this test, so it deliberately times out still Pending).
        var json = await dispatcher.DispatchAsync(PollRequest(1, "exec-1", timeoutMs: 50));

        Assert.Contains("\"status\":\"pending\"", json);
        Assert.Contains("\"execution_id\":\"exec-1\"", json);
    }

    [Fact]
    public async Task PollExecution_ReachesTerminalStateBeforeTimeout_ReturnsPromptly_DoesNotWaitOutTheFullTimeout()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var now = DateTimeOffset.UtcNow;
        executionManager.Start("exec-1", "1 + 1", 600_000, now);
        executionManager.MarkRunning("exec-1", now);
        executionManager.CompleteSuccess("exec-1", now, result: 2, stdOut: null, notices: Array.Empty<DiagnosticRecord>());

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var json = await dispatcher.DispatchAsync(PollRequest(1, "exec-1", timeoutMs: 30_000));
        stopwatch.Stop();

        Assert.Contains("\"status\":\"success\"", json);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"poll_execution took {stopwatch.Elapsed} for an already-terminal execution -- it must return promptly, not wait out timeout_ms.");
    }

    [Fact]
    public async Task PollExecution_TimeoutMsExceedsCeiling_IsClamped_DoesNotThrowOrHang()
    {
        // Independent PR review finding: an unbounded caller-supplied timeout_ms (long.MaxValue here)
        // used to push _now().AddMilliseconds past DateTimeOffset's representable range, throwing
        // ArgumentOutOfRangeException, instead of the loop ever getting a chance to return a normal
        // pending/timeout response. The injected clock only advances via the injected _delay (rather than
        // real Task.Delay), so this also proves the deadline-vs-clock loop genuinely terminates instead of
        // relying on wall-clock time to eventually catch up to a real clock -- the fix this same PR made
        // to stop mixing an injectable _now with a non-injectable Task.Delay.
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var now = DateTimeOffset.UtcNow;
        var dispatcher = new RequestDispatcher(
            executionManager,
            bridge,
            NewScriptExecutor(),
            now: () => now,
            delay: _ => { now = now.AddMilliseconds(200); return Task.CompletedTask; });

        executionManager.Start("exec-1", "1 + 1", 600_000, now);

        var json = await dispatcher.DispatchAsync(PollRequest(1, "exec-1", timeoutMs: long.MaxValue));

        Assert.Contains("\"status\":\"pending\"", json);
    }

    [Fact]
    public async Task PollExecution_DeadlineElapsedWhileStillPending_AttachesWindowInventoryNotice()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var now = DateTimeOffset.UtcNow;
        var windowInventory = new FakeWindowInventory { Windows = new[] { new WindowInfo("Warning", "#32770", Array.Empty<string>()) } };
        var dispatcher = new RequestDispatcher(
            executionManager,
            bridge,
            NewScriptExecutor(),
            now: () => now,
            delay: _ => { now = now.AddMilliseconds(200); return Task.CompletedTask; },
            windowInventory: windowInventory);

        executionManager.Start("exec-1", "1 + 1", 600_000, now);

        var json = await dispatcher.DispatchAsync(PollRequest(1, "exec-1", timeoutMs: 500));

        Assert.Contains("\"status\":\"pending\"", json);
        Assert.Contains("\"code\":\"window-inventory-timeout-fallback\"", json);
        Assert.Contains("\"title\":\"Warning\"", json);
    }

    [Fact]
    public async Task PollExecution_ReachesTerminalBeforeDeadline_DoesNotAttachWindowInventory()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var windowInventory = new FakeWindowInventory { Windows = new[] { new WindowInfo("Warning", "#32770", Array.Empty<string>()) } };
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor(), windowInventory: windowInventory);

        var dispatchTask = dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", "1 + 1"));
        bridge.OnExecute(NewUiApp());
        var json = await dispatchTask;

        Assert.Contains("\"status\":\"success\"", json);
        Assert.DoesNotContain("window-inventory-timeout-fallback", json);
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFoundError()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var request = JsonRpcRequest.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"list_instances\",\"params\":{}}");
        var json = await dispatcher.DispatchAsync(request);

        Assert.Contains("\"error\":{", json);
        Assert.Contains("-32601", json);
    }
}
