using System;
using System.Linq;
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

    private static TransactionScriptExecutor NewScriptExecutor() => new(new RoslynScriptRunner(additionalMetadataReferences: RevitApiReference.References));

    // document_id defaults to the fake active document's own identity (FakeDocumentAdapter's default
    // DocumentId) -- document_id ROUTES now, so a request naming an id no open document has would be
    // document-not-found, and these tests' requests address the document they mean, like a real agent.
    private static JsonRpcRequest ExecuteScriptRequest(int id, string executionId, string script, long timeoutMs = 30_000, long maxDurationMs = 600_000, string documentId = "doc-fake0000000000") =>
        Parse(new
        {
            jsonrpc = "2.0",
            id,
            method = "execute_script",
            @params = new { execution_id = executionId, document_id = documentId, script, timeout_ms = timeoutMs, max_duration_ms = maxDurationMs },
        });

    private static JsonRpcRequest PollRequest(int id, string executionId, long timeoutMs = 30_000) =>
        Parse(new { jsonrpc = "2.0", id, method = "poll_execution", @params = new { execution_id = executionId, timeout_ms = timeoutMs } });

    private static JsonRpcRequest CancelRequest(int id, string executionId) =>
        Parse(new { jsonrpc = "2.0", id, method = "cancel_execution", @params = new { execution_id = executionId } });

    private static JsonRpcRequest Parse(object envelope) => JsonRpcRequest.Parse(JsonSerializer.Serialize(envelope));

    private static FakeUiApplicationAdapter NewUiApp(FakeDocumentAdapter? document = null) =>
        new() { ActiveUiDocument = new FakeUiDocumentAdapter { Document = document ?? new FakeDocumentAdapter() } };

    // ------------------------------------------------------------------------------------------
    // document_id ROUTING (v1 integrated review -- the advertised-but-ignored parameter is real now)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteScript_DocumentIdOfANonActiveDocument_RoutesTheScriptToIt()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var activeDocument = new FakeDocumentAdapter { DocumentId = "doc-active000000000" };
        var routedDocument = new FakeDocumentAdapter { DocumentId = "doc-routed000000000" };
        var uiApp = new FakeUiApplicationAdapter
        {
            ActiveUiDocument = new FakeUiDocumentAdapter { Document = activeDocument },
            FindOpenDocumentHandler = id => id == "doc-routed000000000" ? routedDocument : null,
        };

        var dispatchTask = dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", "1 + 1", documentId: "doc-routed000000000"));
        bridge.OnExecute(uiApp);
        var json = await dispatchTask;

        Assert.Contains("\"status\":\"success\"", json);
        // The ambient transaction opened on the ROUTED document -- the script ran against the
        // document the request addressed, and the active document was never touched. This pair of
        // assertions is the entire point of routing: before it, the transaction always landed on the
        // active document no matter what the request said.
        Assert.NotNull(routedDocument.LastTransaction);
        Assert.Equal(new[] { "Start", "Commit", "Dispose" }, routedDocument.LastTransaction!.Calls);
        Assert.Null(activeDocument.LastTransaction);
    }

    [Fact]
    public async Task ExecuteScript_UnknownDocumentId_ReportsDocumentNotFoundWithCandidates_NeverASilentFallback()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var activeDocument = new FakeDocumentAdapter { DocumentId = "doc-active000000000" };
        var uiApp = new FakeUiApplicationAdapter
        {
            ActiveUiDocument = new FakeUiDocumentAdapter { Document = activeDocument },
            OpenDocuments = new[] { new OpenDocumentInfo("doc-active000000000", "Active.rvt", IsActive: true) },
        };

        var dispatchTask = dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", "1 + 1", documentId: "doc-closed999999999"));
        bridge.OnExecute(uiApp);
        var json = await dispatchTask;

        Assert.Contains("\"status\":\"error\"", json);
        Assert.Contains("\"code\":\"document-not-found\"", json);
        Assert.Contains("doc-closed999999999", json);
        // Candidates: the error names what IS addressable (PRD §01), so the agent corrects without a
        // list_instances round trip.
        Assert.Contains("doc-active000000000", json);
        Assert.Contains("Active.rvt", json);
        // The silent-fallback hazard routing exists to end: the active document must NOT have run
        // anything.
        Assert.Null(activeDocument.LastTransaction);
    }

    [Fact]
    public async Task ExecuteScript_OmittedDocumentId_KeepsTheActiveDocumentBehavior()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var activeDocument = new FakeDocumentAdapter { DocumentId = "doc-active000000000" };
        var uiApp = new FakeUiApplicationAdapter
        {
            ActiveUiDocument = new FakeUiDocumentAdapter { Document = activeDocument },
        };

        var request = Parse(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "execute_script",
            @params = new { execution_id = "exec-1", script = "1 + 1", timeout_ms = 30_000, max_duration_ms = 600_000 },
        });
        var dispatchTask = dispatcher.DispatchAsync(request);
        bridge.OnExecute(uiApp);
        var json = await dispatchTask;

        Assert.Contains("\"status\":\"success\"", json);
        Assert.NotNull(activeDocument.LastTransaction);
    }

    [Fact]
    public async Task ExecuteScript_Success_ReturnsSuccessResultWithTheReturnValue()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var dispatchTask = dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", "1 + 1"));
        bridge.OnExecute(NewUiApp());

        var json = await dispatchTask;

        Assert.Contains("\"status\":\"success\"", json);
        Assert.Contains("\"execution_id\":\"exec-1\"", json);
        Assert.Contains("\"return_value\":\"2\"", json);
    }

    /// <summary>
    /// Issue #117, end to end across the seam that produced it: a real script, compiled and run, through
    /// SafeFormatReturnValue, into the record, out as wire JSON. The reported script came back as
    /// "System.Collections.Generic.List`1[&lt;&gt;f__AnonymousType0#1[...]]" -- a type name an agent had
    /// no way to distinguish from data. ReturnValueFormatterTests covers the formatting rules in
    /// isolation; this one exists because the defect was only visible from the wire.
    /// </summary>
    [Fact]
    public async Task ExecuteScript_ReturningAnonymousProjections_PutsTheDataOnTheWire_NotATypeName()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        // Enumerable.ToList spelled out: RoslynScriptRunner imports only "System", and the List<T> (not
        // the array) is the shape the issue reported.
        var script = "return System.Linq.Enumerable.ToList(new[] { new { Name = \"Level 1\", Elevation = 0.0 } });";
        var dispatchTask = dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", script));
        bridge.OnExecute(NewUiApp());

        var json = await dispatchTask;
        using var parsed = JsonDocument.Parse(json);
        var returnValue = parsed.RootElement.GetProperty("result").GetProperty("return_value").GetString();

        Assert.Contains("\"Name\":\"Level 1\"", returnValue);
        Assert.Contains("\"Elevation\":0", returnValue);
        Assert.DoesNotContain("AnonymousType", returnValue);
    }

    /// <summary>
    /// The other half of #117: stdout and the returned value are separate wire fields now. What made the
    /// mixing hard to spot live is that the interleaved lines were Revit's own console writes, not the
    /// script's -- so a script that wrote nothing still had noise ahead of its answer.
    /// </summary>
    [Fact]
    public async Task ExecuteScript_ScriptThatWritesAndReturns_KeepsStdOutOutOfTheReturnValue()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var script = "System.Console.WriteLine(\"PlayerServer:Warning:No subscriber registered.\"); return \"the answer\";";
        var dispatchTask = dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", script));
        bridge.OnExecute(NewUiApp());

        var json = await dispatchTask;
        using var parsed = JsonDocument.Parse(json);
        var result = parsed.RootElement.GetProperty("result");

        Assert.Equal("the answer", result.GetProperty("return_value").GetString());
        Assert.Contains("PlayerServer", result.GetProperty("output").GetString()!);
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

    // PRD §01/§14, from an independent PR review: the two denylist refusals define their own codes and
    // skill.md tells agents to match on them -- but every script failure was reported with a hardcoded
    // code of "script-execution-failed", so those codes only ever appeared as a SUBSTRING of `message`
    // and never in the field that carries them. These two cases assert the record's actual `code`, which
    // is the thing that was wrong; asserting the message alone is what let it pass unnoticed.

    [Fact]
    public async Task ExecuteScript_DeniedByTheDenylist_ReportsItsOwnCodeAndRemedy()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var dispatchTask = dispatcher.DispatchAsync(
            ExecuteScriptRequest(1, "exec-1", "new Autodesk.Revit.DB.Transaction(Document, \"x\");"));
        bridge.OnExecute(NewUiApp());

        var json = await dispatchTask;

        Assert.Contains("\"status\":\"error\"", json);
        Assert.Contains($"\"code\":\"{ScriptApiDenylistViolationException.DeniedCode}\"", json);
        Assert.DoesNotContain("\"code\":\"script-execution-failed\"", json);
        // Unconditional refusal, so the remedy is "change the script", never "retry with a flag".
        Assert.Contains("no argument to execute_script permits it", json);
        Assert.DoesNotContain("confirm_lifecycle_actions", json);
    }

    /// <summary>
    /// Issue #84, the reported case reproduced verbatim: a script that guesses `doc` for the document
    /// global. Before this, the failure came back as the generic script-execution-failed with a null
    /// remedy, so the only information an agent got was Roslyn's own "The name 'doc' does not exist in the
    /// current context" -- true, unhelpful, and with no path to the name that DOES exist.
    /// </summary>
    [Fact]
    public async Task ExecuteScript_UnknownName_NamesTheGlobalsThatDoExist()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var dispatchTask = dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", "return doc.Title;"));
        bridge.OnExecute(NewUiApp());

        var json = await dispatchTask;

        Assert.Contains("\"status\":\"error\"", json);
        Assert.Contains("\"code\":\"script-compilation-failed\"", json);
        Assert.DoesNotContain("\"code\":\"script-execution-failed\"", json);

        // Asserted against the PARSED remedy, not the raw JSON: System.Text.Json's default encoder
        // escapes an apostrophe to \u0027, so a raw Contains("'doc'") can never match however correct the
        // remedy is. That cost a debugging round; the parsed form is also simply the stronger assertion,
        // since it proves the text landed in the remedy field rather than anywhere in the envelope.
        var remedy = string.Join(" ", ParseRemedy(json));

        // The name the agent actually used, so the remedy is visibly about THIS script...
        Assert.Contains("'doc'", remedy);
        // ...and every name it could have used instead, from the reflected list rather than a copy.
        foreach (var global in ScriptGlobals.GlobalNames)
        {
            Assert.Contains(global, remedy);
        }

        // Routes onward to the tool that explains them, and closes off the tool that never will.
        Assert.Contains("get_skills", remedy);
        Assert.Contains("search_functions", remedy);
    }

    /// <summary>The <c>result.error.remedy</c> array, or empty when the response carries no remedy.</summary>
    private static string[] ParseRemedy(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("error", out var error)
            || !error.TryGetProperty("remedy", out var remedy))
        {
            return Array.Empty<string>();
        }

        return remedy.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
    }

    /// <summary>
    /// The globals list is attached to CS0103 specifically, not to every compile failure. An ordinary
    /// syntax error has nothing to do with globals, and eleven names on every such error is noise that
    /// would train an agent to skip the remedy field.
    /// </summary>
    [Fact]
    public async Task ExecuteScript_SyntaxError_ReportsCompilationFailedWithoutTheGlobalsList()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var dispatchTask = dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", "return 1 +;"));
        bridge.OnExecute(NewUiApp());

        var json = await dispatchTask;

        Assert.Contains("\"code\":\"script-compilation-failed\"", json);
        Assert.Empty(ParseRemedy(json));
    }

    [Fact]
    public async Task ExecuteScript_LifecycleMemberWithoutConfirmation_ReportsItsOwnCodeAndTheResendRemedy()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        // ExecuteScriptRequest sends no confirm_lifecycle_actions -- the refusing case by design.
        var dispatchTask = dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", "Document.Close();"));
        bridge.OnExecute(NewUiApp());

        var json = await dispatchTask;

        Assert.Contains("\"status\":\"error\"", json);
        Assert.Contains($"\"code\":\"{ScriptApiDenylistViolationException.ConfirmationRequiredCode}\"", json);
        Assert.DoesNotContain("\"code\":\"script-execution-failed\"", json);
        // The single most obvious next step in the connector, and it used to be reported with none.
        Assert.Contains("confirm_lifecycle_actions: true", json);
        Assert.Contains("Autodesk.Revit.DB.Document.Close", json);
    }

    [Fact]
    public async Task ExecuteScript_ContainingAwait_ReportsItsOwnCode()
    {
        // Same call site, same gap, same fix: this exception also carries its own code and was also
        // being flattened to script-execution-failed.
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        var dispatchTask = dispatcher.DispatchAsync(
            ExecuteScriptRequest(1, "exec-1", "await System.Threading.Tasks.Task.Delay(1); return 1;"));
        bridge.OnExecute(NewUiApp());

        var json = await dispatchTask;

        Assert.Contains($"\"code\":\"{ScriptAwaitNotAllowedException.Code}\"", json);
    }

    [Fact]
    public async Task ExecuteScript_NoActiveDocument_ReturnsErrorWithoutTouchingScriptExecutor()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var dispatcher = new RequestDispatcher(executionManager, bridge, NewScriptExecutor());

        // document_id deliberately empty: naming an id against a document-less instance now gets the
        // more specific document-not-found (with an empty candidates list); no-active-document is the
        // omitted-id shape's error.
        var dispatchTask = dispatcher.DispatchAsync(ExecuteScriptRequest(1, "exec-1", "1 + 1", documentId: ""));
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
        Assert.Contains("unknown-execution-id", json);
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
        Assert.Contains("unknown-execution-id", json);
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
        executionManager.CompleteSuccess("exec-1", now, result: "2", stdOut: null, notices: Array.Empty<DiagnosticRecord>());

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
    public async Task PollExecution_DeadlineElapsedWithAllowlistedDialogDismissed_AttachesDialogAutoDismissedNotice()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var now = DateTimeOffset.UtcNow;
        // §07 v2: the adapter auto-dismissed an allowlisted #32770 dialog; the fake reports it on Dismissed
        // (no present windows), and the timeout branch must surface it as a dialog-auto-dismissed notice.
        var windowInventory = new FakeWindowInventory
        {
            Dismissed = new[] { new DismissedDialog("#32770", "Virtual Memory - High Usage") },
        };
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
        Assert.Contains("\"code\":\"dialog-auto-dismissed\"", json);
        Assert.Contains("Virtual Memory - High Usage", json);
        // The dispatcher must consult the Core allowlist policy, not some ad-hoc predicate.
        Assert.NotNull(windowInventory.LastShouldDismiss);
        Assert.True(windowInventory.LastShouldDismiss!("#32770", "Virtual Memory - High Usage"));
        Assert.False(windowInventory.LastShouldDismiss!("#32770", "Something Else"));
    }

    [Fact]
    public async Task PollExecution_AllowlistedDialogDismissed_ReportedEvenWhenInventoryAbandonedByWireBudget()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var now = DateTimeOffset.UtcNow;
        // The whole point of the side channel: the inventory pass is abandoned by the #138 wire-budget cap
        // (its window-inventory-timeout-fallback notice is dropped), but the auto-dismiss happened early in
        // the pass, and §01 requires that action still be reported. A gate makes the abandon DETERMINISTIC
        // regardless of thread-pool contention: the pass fires its dismissal, then blocks on the gate so the
        // wire-budget Task.Delay always wins the race; the fake surfaces the dismissal through the side
        // channel before blocking.
        using var gate = new System.Threading.ManualResetEventSlim(false);
        var windowInventory = new FakeWindowInventory
        {
            Dismissed = new[] { new DismissedDialog("#32770", "Virtual Memory - High Usage") },
            Gate = gate,
        };
        var dispatcher = new RequestDispatcher(
            executionManager,
            bridge,
            NewScriptExecutor(),
            now: () => now,
            delay: _ => { now = now.AddMilliseconds(200); return Task.CompletedTask; },
            windowInventory: windowInventory);

        executionManager.Start("exec-1", "1 + 1", 600_000, now);

        try
        {
            var json = await dispatcher.DispatchAsync(PollRequest(1, "exec-1", timeoutMs: 500));

            Assert.Contains("\"status\":\"pending\"", json);
            // The dismissal survived the abandon; the diagnostic inventory notice did not.
            Assert.Contains("\"code\":\"dialog-auto-dismissed\"", json);
            Assert.DoesNotContain("window-inventory-timeout-fallback", json);
        }
        finally
        {
            gate.Set(); // release the still-blocked inventory pass so its thread is freed
        }
    }

    // #136: the §07 window inventory reads window text via a blocking Win32 call against the very UI thread
    // it inspects, so under a long-running script it can take seconds -- and it ran SYNCHRONOUSLY on the
    // pending-response path, intermittently blowing the broker's timeout_ms + 5s wire budget: the diagnostic
    // timing out the very wire call it exists to explain. These pin the budget math and the hard cap.

    [Theory]
    // handler already spent ~timeout_ms; plenty of buffer left -> the hard cap, which is what runs.
    [InlineData(2000, 2000, 1500)]
    [InlineData(0, 2000, 1500)]
    // just enough left to bother with (>= InventoryMinBudgetMs=250)
    [InlineData(5000, 2000, 500)]
    // budget nearly gone -> below the min, so the caller skips the inventory
    [InlineData(5300, 2000, 200)]
    // budget already blown -> negative, which is still "skip", never a wait
    [InlineData(6000, 2000, -500)]
    // an unbounded caller timeout_ms is clamped before it inflates the budget
    [InlineData(0, long.MaxValue, 1500)]
    public void ComputeInventoryBudgetMs_SizesToRemainingWireBudget(long handlerElapsedMs, long timeoutMs, long expected)
    {
        Assert.Equal(expected, RequestDispatcher.ComputeInventoryBudgetMs(handlerElapsedMs, timeoutMs));
    }

    [Fact]
    public async Task PollExecution_SlowWindowInventory_DoesNotBlockResponsePastWireBudget()
    {
        var executionManager = NewExecutionManager();
        var bridge = new ExternalEventBridge<ScriptExecutionOutcome>(new FakeExternalEventRaiser());
        var now = DateTimeOffset.UtcNow;
        // The inventory blocks for 8s -- exactly #136: a busy UI thread making the diagnostic take seconds.
        // The response must abandon it and return the pending status, not wait it out.
        var windowInventory = new FakeWindowInventory
        {
            BlockFor = TimeSpan.FromSeconds(8),
            Windows = new[] { new WindowInfo("Warning", "#32770", Array.Empty<string>()) },
        };
        var dispatcher = new RequestDispatcher(
            executionManager,
            bridge,
            NewScriptExecutor(),
            now: () => now,
            delay: _ => { now = now.AddMilliseconds(200); return Task.CompletedTask; },
            windowInventory: windowInventory);

        executionManager.Start("exec-1", "1 + 1", 600_000, now);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var json = await dispatcher.DispatchAsync(PollRequest(1, "exec-1", timeoutMs: 500));
        sw.Stop();

        Assert.Contains("\"status\":\"pending\"", json);
        // Abandoned, not waited on: no notice, and it returned well inside the 8s block (~the hard cap).
        Assert.DoesNotContain("window-inventory-timeout-fallback", json);
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(5),
            $"poll_execution waited {sw.Elapsed} on a slow window inventory -- the diagnostic must never delay the response past the wire budget (#136).");
        // It WAS entered (so the notice's absence is the cap firing, not the inventory being skipped): the
        // enumerate call is in flight on its own task even though the response already returned.
        Assert.Equal(1, windowInventory.EnumerateCallCount);
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
