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

    public Task<string> DispatchAsync(JsonRpcRequest request) => request.Method switch
    {
        "execute_script" => HandleExecuteScriptAsync(request),
        "poll_execution" => Task.FromResult(HandlePollExecution(request)),
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

        _executionManager.MarkRunning(executionId, _now());

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

    private string HandlePollExecution(JsonRpcRequest request)
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

        var record = _executionManager.Poll(executionId);
        return record is not null
            ? ExecutionResultMessage.FromRecord(request.Id, record)
            : JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, $"execution_id '{executionId}' is not known to this add-in instance.", UnknownExecutionDiagnostic(executionId));
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

        var record = _executionManager.Poll(executionId);
        if (record is not null && outcome == CancellationRequestOutcome.Acknowledged && record.Status == ExecutionStatus.Cancelled)
        {
            // Hard requirement 3: RequestCancellation resolving straight to Cancelled (rather than merely
            // stamping CancellationRequestedAt and leaving the record Running) means the execution was
            // still Pending -- ExecutionManager.ApplyCancellation's own doc comment explains why that
            // specific transition is the signal that a queued-but-not-yet-run work item may still be
            // sitting in the bridge, so it must be abandoned here rather than left to fire later.
            _bridge.Abandon();
        }

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
