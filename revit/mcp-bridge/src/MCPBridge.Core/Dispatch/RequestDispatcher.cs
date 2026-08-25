using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MCPBridge.Core.Diagnostics;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Protocol;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Dispatch;

/// <summary>
/// Routes one already-parsed incoming JSON-RPC request (<see cref="JsonRpcRequest"/>) from the broker to
/// the right <see cref="ExecutionManager"/> call, drives the actual script run through
/// <see cref="ExternalEventBridge{TResult}"/>/<see cref="TransactionScriptExecutor"/>, and serializes
/// whatever comes back into the wire response shape (<see cref="ExecutionResultMessage"/>/
/// <see cref="JsonRpcErrorMessage"/>). Deliberately has no knowledge of sockets/framing -- BridgeHost owns
/// reading NDJSON lines off the wire and writing this class's output back; this class's job stops at
/// "given a parsed request, what response string should go back," which keeps it testable with the same
/// fakes (<see cref="MCPBridge.RevitAdapter.IExternalEventRaiser"/>, <see cref="IUiApplicationAdapter"/>,
/// etc.) the rest of this codebase already uses -- no real socket needed.
///
/// Encodes every hard requirement from BridgeHost.cs's TODO (second review pass):
/// <list type="number">
/// <item>The queued work item checks <see cref="ExecutionManager.GetCancellationToken"/> before touching
/// the model at all, and resolves straight to Cancelled if it's already set.</item>
/// <item>The bridge's RunAsync Task gets a continuation that calls
/// <see cref="ExecutionManager.CompleteError"/> on ANY fault, including
/// <see cref="ExternalEventRaiseDeniedException"/>, not just exceptions the work item itself throws.</item>
/// <item>cancel_execution calls <see cref="ExternalEventBridge{TResult}.Abandon"/> when the cancellation
/// resolved a still-Pending execution directly to Cancelled, so a stale queued raise can't wedge the
/// bridge.</item>
/// <item><see cref="ExecutionManager.Start"/>'s ArgumentException (a malformed/colliding execution_id) is
/// caught and converted into a JSON-RPC error response, never left to propagate.</item>
/// </list>
/// </summary>
public sealed class RequestDispatcher
{
    /// <summary>PRD §06: "sensible default, e.g. 30000, if omitted" for execute_script/poll_execution's timeout_ms.</summary>
    private const long DefaultTimeoutMs = 30_000;

    /// <summary>PRD §06: "default generous, e.g. 10 minutes" for execute_script's max_duration_ms.</summary>
    private const long DefaultMaxDurationMs = 600_000;

    private readonly ExecutionManager _executionManager;
    private readonly ExternalEventBridge<ScriptExecutionOutcome> _bridge;
    private readonly TransactionScriptExecutor _scriptExecutor;
    private readonly Func<DateTimeOffset> _now;

    public RequestDispatcher(
        ExecutionManager executionManager,
        ExternalEventBridge<ScriptExecutionOutcome> bridge,
        TransactionScriptExecutor scriptExecutor,
        Func<DateTimeOffset>? now = null)
    {
        _executionManager = executionManager;
        _bridge = bridge;
        _scriptExecutor = scriptExecutor;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>How often <see cref="HandlePollExecutionAsync"/> re-checks the record while waiting out timeout_ms.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public Task<string> DispatchAsync(JsonRpcRequest request) => request.Method switch
    {
        "execute_script" => HandleExecuteScriptAsync(request),
        "poll_execution" => HandlePollExecutionAsync(request),
        "cancel_execution" => Task.FromResult(HandleCancelExecution(request)),
        _ => Task.FromResult(JsonRpcErrorMessage.ToJson(
            request.Id,
            JsonRpcErrorCode.MethodNotFound,
            $"unknown method '{request.Method}'",
            null)),
    };

    private async Task<string> HandleExecuteScriptAsync(JsonRpcRequest request)
    {
        string executionId;
        string script;
        long maxDurationMs;
        long timeoutMs;
        try
        {
            executionId = request.GetRequiredString("execution_id");
            script = request.GetRequiredString("script");
            maxDurationMs = request.GetOptionalInt64("max_duration_ms", DefaultMaxDurationMs);
            timeoutMs = request.GetOptionalInt64("timeout_ms", DefaultTimeoutMs);

            // KNOWN PHASE 1 LIMITATION (second live-wiring review finding): document_id is part of the wire
            // contract (the broker always sends it) but is not read or enforced here -- every script runs
            // against whatever IUiApplicationAdapter.ActiveUiDocument happens to be (see RunScriptWorkItem
            // below), regardless of which document_id was requested. Real per-document routing needs
            // Application.Documents (only reachable via DocumentSnapshotHandler's raw-Revit-API path, not
            // the IUiApplicationAdapter seam RunScriptWorkItem goes through) plus a way to select/activate
            // the target document -- deferred to Phase 2/3 alongside the rest of multi-document support.
            // Silently ignoring a mismatch (rather than erroring) is a deliberate Phase 1 choice: single
            // active-document Revit instances (the common case) work correctly either way.
        }
        catch (JsonRpcParamException ex)
        {
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, null);
        }

        ExecuteOutcome outcome;
        try
        {
            outcome = _executionManager.Start(executionId, script, maxDurationMs, _now());
        }
        catch (ArgumentException ex)
        {
            // Hard requirement 4: Start's validation failure (null/empty/colliding executionId) must
            // become a JSON-RPC error response, not propagate and kill the connection.
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, null);
        }

        switch (outcome.Kind)
        {
            case ExecuteOutcomeKind.Busy:
                return ExecutionResultMessage.Busy(request.Id, outcome.Record!.ExecutionId);
            case ExecuteOutcomeKind.InstanceUnrecoverable:
                return ExecutionResultMessage.FromInstanceUnrecoverable(request.Id, outcome.Diagnostic!);
        }

        var workTask = RunScriptWorkItemAsync(executionId, script);

        // PRD §06: a script finishing inside timeout_ms returns the completed result inline; one that
        // doesn't returns the current {status, execution_id} instead of hanging the call. The timeout
        // timer is cancelled as soon as workTask wins the race (the common case -- most scripts finish
        // well under timeout_ms) so it doesn't sit running in the background for the rest of its duration.
        using var timeoutCts = new CancellationTokenSource();
        var timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, timeoutMs)), timeoutCts.Token);
        var first = await Task.WhenAny(workTask, timeoutTask).ConfigureAwait(false);
        if (first == workTask)
        {
            timeoutCts.Cancel();

            // Already handled internally (RunScriptWorkItemAsync's continuation never lets this Task
            // fault) -- awaiting it here just keeps any exception from becoming an unobserved task
            // exception, it never throws past this point.
            await workTask.ConfigureAwait(false);
        }

        var record = _executionManager.Poll(executionId);
        return record is not null
            ? ExecutionResultMessage.FromRecord(request.Id, record)
            : ExecutionResultMessage.Busy(request.Id, executionId); // defensive fallback; should be unreachable
    }

    private Task RunScriptWorkItemAsync(string executionId, string scriptText)
    {
        var runTask = _bridge.RunAsync(uiApplication => RunScriptWorkItem(executionId, scriptText, uiApplication));

        // Hard requirement 2: ANY fault on this Task -- including ExternalEventRaiseDeniedException, which
        // RunScriptWorkItem below never gets a chance to observe or react to since it never even ran --
        // must resolve the execution via CompleteError so ExecutionManager's _active slot and this
        // execution's CancellationTokenSource don't dangle forever.
        return runTask.ContinueWith(
            t =>
            {
                if (!t.IsFaulted)
                {
                    return;
                }

                var ex = t.Exception!.GetBaseException();
                var diagnostic = DiagnosticRecord.Create(
                    DiagnosticSeverity.Error,
                    "execution-bridge-fault",
                    DiagnosticSource.Execution,
                    $"execution {executionId} could not run: {ex.Message}",
                    detail: new Dictionary<string, object?> { ["execution_id"] = executionId },
                    remedy: null);
                _executionManager.CompleteError(executionId, _now(), diagnostic, stdOut: null);
            },
            TaskScheduler.Default);
    }

    private ScriptExecutionOutcome RunScriptWorkItem(string executionId, string scriptText, IUiApplicationAdapter uiApplication)
    {
        // Hard requirement 1: check cancellation before touching the model at all -- a still-Pending
        // execution can have been resolved directly to Cancelled by ExecutionManager.ApplyCancellation
        // while this work item was still sitting queued in ExternalEventBridge._pending (nothing can
        // un-queue it from Revit's side once raised).
        var cancellationToken = _executionManager.GetCancellationToken(executionId);
        if (cancellationToken.IsCancellationRequested)
        {
            _executionManager.CompleteCancelled(executionId, _now(), stdOut: null);
            return ScriptExecutionOutcome.Cancelled("");
        }

        // Second live-wiring review finding: MarkRunning's return value (non-null means the record already
        // went terminal by the time this ran -- e.g. cancelled in the window between OnExecute clearing
        // ExternalEventBridge._pending and this method reaching MarkRunning, since the cancellation-token
        // check above already passed before that happened) was previously discarded. Discarding it meant
        // the script ran against the model anyway for an execution the broker had already been told was
        // Cancelled. Bail out here instead -- the record is already terminal, so there's nothing left to
        // transition to; just don't touch the model.
        if (_executionManager.MarkRunning(executionId, _now()) is not null)
        {
            return ScriptExecutionOutcome.Cancelled("");
        }

        var uiDocument = uiApplication.ActiveUiDocument;
        var document = uiDocument?.Document;
        if (document is null)
        {
            var diagnostic = DiagnosticRecord.Create(
                DiagnosticSeverity.Error,
                "no-active-document",
                DiagnosticSource.Execution,
                $"execution {executionId} could not run: this Revit instance has no active document.",
                detail: new Dictionary<string, object?> { ["execution_id"] = executionId },
                remedy: new[] { "Open a document in this Revit instance and retry." });
            _executionManager.CompleteError(executionId, _now(), diagnostic, stdOut: null);
            return ScriptExecutionOutcome.Failed(new InvalidOperationException(diagnostic.Message), "");
        }

        // .GetAwaiter().GetResult() is deadlock-safe here only because RoslynScriptRunner rejects any
        // script containing its own top-level `await` before compiling it -- see
        // ExternalEventBridge<TResult>'s own doc comment and RoslynScriptRunner.RejectTopLevelAwait.
        var outcome = _scriptExecutor
            .ExecuteAsync(document, uiApplication, uiDocument, scriptText, cancellationToken)
            .GetAwaiter().GetResult();

        if (outcome.WasCancelled)
        {
            _executionManager.CompleteCancelled(executionId, _now(), outcome.StdOut);
        }
        else if (outcome.Success)
        {
            _executionManager.CompleteSuccess(executionId, _now(), outcome.ReturnValue, outcome.StdOut, Array.Empty<DiagnosticRecord>());
        }
        else
        {
            var diagnostic = DiagnosticRecord.Create(
                DiagnosticSeverity.Error,
                "script-execution-failed",
                DiagnosticSource.Execution,
                outcome.Exception?.Message ?? $"execution {executionId} failed with no exception detail.",
                detail: new Dictionary<string, object?> { ["execution_id"] = executionId },
                remedy: null);
            _executionManager.CompleteError(executionId, _now(), diagnostic, outcome.StdOut);
        }

        return outcome;
    }

    /// <summary>
    /// Second live-wiring review finding: poll_execution previously ignored timeout_ms entirely and
    /// answered instantly with whatever the current status was -- against execution.go's own package doc
    /// ("the add-in is expected to wait internally ... up to the caller's timeout_ms before answering"),
    /// this turned a long-poll into a hot spin (the agent's own client re-polls immediately, so a 30s
    /// timeout_ms returned in ~1ms). Now waits, re-checking at a fixed interval, until either the execution
    /// reaches a terminal state or timeout_ms elapses -- an in-memory poll rather than a real event/signal,
    /// but bounded and cheap, and matches the documented contract without new signaling infrastructure.
    /// </summary>
    private async Task<string> HandlePollExecutionAsync(JsonRpcRequest request)
    {
        string executionId;
        long timeoutMs;
        try
        {
            executionId = request.GetRequiredString("execution_id");
            timeoutMs = request.GetOptionalInt64("timeout_ms", DefaultTimeoutMs);
        }
        catch (JsonRpcParamException ex)
        {
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, null);
        }

        var deadline = _now().AddMilliseconds(Math.Max(0, timeoutMs));
        while (true)
        {
            var record = _executionManager.Poll(executionId);
            if (record is null)
            {
                return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, $"execution_id '{executionId}' is not known to this add-in instance.", UnknownExecutionDiagnostic(executionId));
            }

            if (record.Status.IsTerminal() || _now() >= deadline)
            {
                return ExecutionResultMessage.FromRecord(request.Id, record);
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    private string HandleCancelExecution(JsonRpcRequest request)
    {
        string executionId;
        try
        {
            executionId = request.GetRequiredString("execution_id");
        }
        catch (JsonRpcParamException ex)
        {
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, null);
        }

        var outcome = _executionManager.RequestCancellation(executionId, _now());
        if (outcome == CancellationRequestOutcome.NotFound)
        {
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, $"execution_id '{executionId}' is not known to this add-in instance.", UnknownExecutionDiagnostic(executionId));
        }

        // Hard requirement 3, fixed per second live-wiring review: gate Abandon() on the outcome
        // RequestCancellation itself reports (AcknowledgedWasPending), NOT on re-Polling the record's
        // resulting status. The two are NOT equivalent: this dispatch loop is fire-and-continue, so a
        // completely different execution can start, run, and reach Cancelled/whatever status in the window
        // between this call's RequestCancellation and its own Poll -- re-inferring "was this a Pending
        // cancel" from Poll's snapshot could then call Abandon() and kill THAT unrelated execution's
        // legitimately-queued work item. AcknowledgedWasPending is set inside ExecutionManager's own lock,
        // at the exact moment of the transition it describes, so there's no such window.
        if (outcome == CancellationRequestOutcome.AcknowledgedWasPending)
        {
            _bridge.Abandon();
        }

        var record = _executionManager.Poll(executionId);

        return record is not null
            ? ExecutionResultMessage.FromRecord(request.Id, record)
            : JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InternalError, $"execution_id '{executionId}' vanished mid-cancellation.", null);
    }

    private static DiagnosticRecord UnknownExecutionDiagnostic(string executionId) => DiagnosticRecord.Create(
        DiagnosticSeverity.Error,
        "unknown_execution_id",
        DiagnosticSource.Execution,
        $"execution_id '{executionId}' is not known to this add-in instance (never started, or evicted from the ring buffer).",
        detail: new Dictionary<string, object?> { ["execution_id"] = executionId },
        remedy: new[] { "Start a new execution with execute_script." });
}
