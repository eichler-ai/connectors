using System.Text.Json;
using Rhino.MCPBridge.Core.Capture;
using Rhino.MCPBridge.Core.Diagnostics;
using Rhino.MCPBridge.Core.Execution;
using Rhino.MCPBridge.Core.Protocol;

namespace Rhino.MCPBridge.Core.Dispatch;

/// <summary>
/// Routes the wire methods a server sends after auth (rhino/docs/PRD.md §06): execute_script,
/// poll_execution, cancel_execution. Ported from the Revit dispatcher's shape with the ExternalEvent
/// replaced by <see cref="IRunLauncher"/> + <see cref="UndoRunExecutor"/>. Every failure is a §01
/// record. Testable with a fake host and an inline launcher.
///
/// Concurrency: the plug-in owns busy state (PRD §05). ExecutionManager admits one run per instance;
/// a second execute_script -- from this or any other server -- gets `busy` with the running id.
/// </summary>
internal sealed class RequestDispatcher
{
    public const long DefaultTimeoutMs = 30_000;
    public const long DefaultMaxDurationMs = 600_000;
    /// <summary>How long a run may sit `pending` because another command holds Rhino before the launcher gives up retrying.</summary>
    public static readonly TimeSpan StartRetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TimeoutCheckInterval = TimeSpan.FromSeconds(1);

    private readonly ExecutionManager _executionManager;
    private readonly UndoRunExecutor _executor;
    private readonly IRunLauncher _launcher;
    private readonly ViewCaptureService? _capture;
    private readonly Func<Func<object>, object>? _onMainThread;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly Action<string> _log;
    private readonly RunLedger _ledger;
    private readonly UndoRedoExecutor _undoRedo;

    public static readonly string[] SupportedMethods = { "execute_script", "poll_execution", "cancel_execution", "capture_view", "undo_redo" };

    /// <summary>undo_redo's timeout bounds: the command is synchronous on the main thread, the wait is for the main-thread hop.</summary>
    public const long UndoMaxTimeoutMs = 30_000, UndoDefaultTimeoutMs = 10_000;

    /// <summary>The per-document last-run ledger (PRD §05), read by the plug-in for register snapshots.</summary>
    public RunLedger Ledger => _ledger;

    /// <summary>"idle" | "busy" | "unrecoverable", for register and ping (PRD §05).</summary>
    public string ExecutionState => _executionManager.ExecutionState;

    /// <summary>Raised after the ledger changed, so the plug-in can re-send register with the new last_run.</summary>
    public event Action? LedgerChanged;

    /// <param name="capture">capture_view's policy; null disables the method (unknown-method).</param>
    /// <param name="onMainThread">Runs a function on the main thread and waits, bounded (the adapter's
    /// IMainThread); capture needs the main thread but no command, since it changes nothing.</param>
    public RequestDispatcher(ExecutionManager executionManager, UndoRunExecutor executor, IRunLauncher launcher, Action<string> log,
        Func<DateTimeOffset>? now = null, Func<TimeSpan, Task>? delay = null,
        ViewCaptureService? capture = null, Func<Func<object>, object>? onMainThread = null, RunLedger? ledger = null)
    {
        _executionManager = executionManager;
        _executor = executor;
        _ledger = ledger ?? new RunLedger();
        _undoRedo = new UndoRedoExecutor(executor.Host, _ledger, executor.Clock);
        _launcher = launcher;
        _capture = capture;
        _onMainThread = onMainThread;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    public Task<string> DispatchAsync(JsonRpcRequest request, CancellationToken cancellationToken) => request.Method switch
    {
        "execute_script" => HandleExecuteScriptAsync(request),
        "poll_execution" => HandlePollExecutionAsync(request),
        "cancel_execution" => Task.FromResult(HandleCancelExecution(request)),
        "capture_view" when _capture is not null && _onMainThread is not null => Task.FromResult(HandleCaptureView(request)),
        "undo_redo" => HandleUndoRedoAsync(request),
        _ => Task.FromResult(UnknownMethod(request)),
    };

    /// <summary>capture_view (PRD §11): serialised with scripts by refusing while one runs (`busy`),
    /// then executed on the main thread with the adapter's bounded wait. Changes nothing in the
    /// document, so it needs no command and no undo entry. The PNGs ride the wire base64-encoded.</summary>
    private string HandleCaptureView(JsonRpcRequest request)
    {
        CaptureRequest req;
        string documentId;
        try
        {
            documentId = request.GetOptionalString("document_id") ?? "";
            req = new CaptureRequest
            {
                Target = request.GetOptionalString("target") ?? "active",
                DisplayMode = request.GetOptionalString("display_mode"),
                Zoom = request.GetOptionalString("zoom") ?? "none",
                Width = request.GetOptionalInt32("width", 0),
                Height = request.GetOptionalInt32("height", 0),
                TransparentBackground = request.GetOptionalBool("transparent_background", false),
                DrawGrid = request.GetOptionalBool("draw_grid", true),
                DrawAxes = request.GetOptionalBool("draw_axes", true),
                Format = request.GetOptionalString("format") ?? "jpeg",
            };
        }
        catch (JsonRpcParamException ex)
        {
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, ex.Diagnostic);
        }

        if (_executionManager.ActiveExecutionId is { } active)
        {
            return ExecutionResultMessage.Busy(request.Id, active);
        }

        try
        {
            var result = (ViewCaptureService.Result)_onMainThread!(() =>
            {
                var document = _executor.Host.ResolveDocument(documentId);
                if (document is null)
                {
                    throw new DocumentNotFoundException(DiagnosticRecord.Create(DiagnosticSeverity.Error, "document-not-found", DiagnosticSource.Execution,
                        documentId.Length == 0 ? "this Rhino instance has no active document" : $"no open document has document_id '{documentId}'",
                        new Dictionary<string, object?> { ["requested_document_id"] = documentId }, new[] { "call list_instances and pick a document_id" }));
                }

                return _capture!.Capture(document.Raw!, req);
            });
            return CaptureResultMessage.ToJson(request.Id, result);
        }
        catch (CaptureRequestException ex)
        {
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, ex.Record);
        }
        catch (DocumentNotFoundException ex)
        {
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, ex.Record);
        }
        catch (Exception ex)
        {
            var rec = DiagnosticRecord.Create(DiagnosticSeverity.Error, "capture-failed", DiagnosticSource.Execution,
                $"capture_view failed: {ex.GetType().Name}: {ex.Message}", null,
                new[] { "if Rhino's main thread is inside a modal or a long command, wait and retry; otherwise the viewport may not be capturable in this display mode" });
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InternalError, rec.Message, rec);
        }
    }

    /// <summary>The periodic sweep the host drives: max_duration auto-cancel and cancellation grace expiry (PRD §06).</summary>
    public void Tick()
    {
        var now = _now();
        var expired = _executionManager.CheckMaxDuration(now);
        if (expired is not null)
        {
            _log($"execution {expired} exceeded max_duration_ms; cancellation requested");
        }

        _executionManager.CheckGraceExpiry(now);
    }

    public static TimeSpan TickInterval => TimeoutCheckInterval;

    private async Task<string> HandleExecuteScriptAsync(JsonRpcRequest request)
    {
        string executionId, script, documentId, language, clientId;
        long maxDurationMs, timeoutMs;
        bool confirm;
        string? label;
        try
        {
            executionId = request.GetRequiredString("execution_id");
            clientId = request.GetOptionalString("agent_client_id") ?? "";
            language = request.GetRequiredString("language");
            script = request.GetRequiredString("script");
            documentId = request.GetOptionalString("document_id") ?? "";
            maxDurationMs = request.GetOptionalInt64("max_duration_ms", DefaultMaxDurationMs);
            timeoutMs = request.GetOptionalInt64("timeout_ms", DefaultTimeoutMs);
            confirm = request.GetOptionalBool("confirm_lifecycle_actions", false);
            label = request.GetOptionalString("label");
        }
        catch (JsonRpcParamException ex)
        {
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, ex.Diagnostic);
        }

        var runner = _executor.Runners.Get(language);
        if (runner is null)
        {
            // Loud, never a silent fallback to the other language (PRD §06).
            var available = _executor.Runners.Languages;
            var rec = DiagnosticRecord.Create(DiagnosticSeverity.Error, "language-not-available", DiagnosticSource.Execution,
                $"language '{language}' is not one this bridge runs; available: {string.Join(", ", available)}.",
                new Dictionary<string, object?> { ["language"] = language, ["available"] = available },
                new[] { $"Resend with language set to one of: {string.Join(", ", available)}." });
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, rec.Message, rec);
        }

        if (runner.UnavailableReason is { } unavailable)
        {
            var rec = DiagnosticRecord.Create(DiagnosticSeverity.Error, "language-not-available", DiagnosticSource.Execution,
                $"language '{language}' cannot run on this instance right now: {unavailable}",
                new Dictionary<string, object?> { ["language"] = language, ["available"] = _executor.Runners.Languages, ["reason"] = unavailable },
                new[] { "If the language is still loading, retry in a few seconds; otherwise use the other language or check the plug-in's connection.log." });
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, rec.Message, rec);
        }

        ExecuteOutcome outcome;
        try
        {
            outcome = _executionManager.Start(executionId, script, maxDurationMs, _now());
        }
        catch (ArgumentException ex)
        {
            var rec = DiagnosticRecord.Create(DiagnosticSeverity.Error, "invalid-execution-id", DiagnosticSource.Execution, ex.Message,
                new Dictionary<string, object?> { ["param"] = "execution_id", ["execution_id"] = executionId },
                new[] { "Mint a fresh, unique execution_id for each execute_script call and echo it unchanged on poll/cancel." });
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, rec);
        }

        switch (outcome.Kind)
        {
            case ExecuteOutcomeKind.Busy:
                return ExecutionResultMessage.Busy(request.Id, outcome.Record!.ExecutionId);
            case ExecuteOutcomeKind.InstanceUnrecoverable:
                return ExecutionResultMessage.FromInstanceUnrecoverable(request.Id, outcome.Diagnostic!);
        }

        // Pre-flight on the connection thread (Revit #67): a compile error, a denied member or an
        // unconfirmed lifecycle call is a property of the text and is refused immediately and
        // deterministically, never queued behind a busy main thread. Only once the runner is warm.
        if (runner.IsWarm)
        {
            var rejection = runner.TryPreflight(script, confirm);
            if (rejection is not null)
            {
                _log($"script rejected pre-flight (execution {executionId}): {rejection.Exception?.GetType().Name}: {rejection.Exception?.Message}");
                Finish(executionId, rejection, clientId, label);
                return ExecutionResultMessage.FromRecord(request.Id, _executionManager.Poll(executionId)!);
            }
        }

        Launch(new UndoRunExecutor.Request
        {
            ExecutionId = executionId,
            ScriptText = script,
            Language = language,
            DocumentId = documentId,
            CancellationToken = _executionManager.GetCancellationToken(executionId),
            ConfirmLifecycleActions = confirm,
            Label = label,
            AgentClientId = clientId,
        }, _now().AddMilliseconds(Math.Max(maxDurationMs, 0)));

        return await WaitAsync(request.Id, executionId, timeoutMs).ConfigureAwait(false);
    }

    /// <summary>Posts the run to the main thread. When Rhino refuses to start our command (a person is
    /// mid-command), the run stays `pending` and is re-posted every StartRetryInterval until it starts,
    /// is cancelled, or its max_duration deadline passes.</summary>
    private void Launch(UndoRunExecutor.Request req, DateTimeOffset deadline)
    {
        _launcher.Post(() =>
        {
            // Cancelled while queued: never touch the model (Revit's hard requirement 1).
            if (req.CancellationToken.IsCancellationRequested)
            {
                _executionManager.CompleteCancelled(req.ExecutionId, _now(), stdOut: null);
                return;
            }

            if (_executionManager.MarkRunning(req.ExecutionId, _now()) is not null)
            {
                return; // already terminal (cancelled in the gap)
            }

            ScriptExecutionOutcome? outcome;
            try
            {
                outcome = _executor.Execute(req);
            }
            catch (Exception ex)
            {
                outcome = ScriptExecutionOutcome.Failed(ex, "");
            }

            if (outcome is null)
            {
                // Could not start: Rhino is inside another command. Back to pending and retry.
                _executionManager.MarkPendingAgain(req.ExecutionId);
                if (_now() >= deadline)
                {
                    Finish(req.ExecutionId, ScriptExecutionOutcome.Failed(
                        new TimeoutException("Rhino was busy with another command for the whole of max_duration_ms; the script never started"), ""), req.AgentClientId, req.Label);
                    return;
                }

                _ = _delay(StartRetryInterval).ContinueWith(_ => Launch(req, deadline), TaskScheduler.Default);
                return;
            }

            Finish(req.ExecutionId, outcome, req.AgentClientId, req.Label);
        });
    }

    private async Task<string> WaitAsync(JsonElement id, string executionId, long timeoutMs)
    {
        var deadline = _now().AddMilliseconds(Math.Clamp(timeoutMs, 0, DefaultMaxDurationMs));
        while (true)
        {
            var record = _executionManager.Poll(executionId);
            if (record is null)
            {
                return JsonRpcErrorMessage.ToJson(id, JsonRpcErrorCode.InvalidParams, $"execution_id '{executionId}' is not known to this bridge.", UnknownExecution(executionId));
            }

            if (record.Status.IsTerminal() || timeoutMs <= 0 || _now() >= deadline)
            {
                return ExecutionResultMessage.FromRecord(id, record);
            }

            await _delay(PollInterval).ConfigureAwait(false);
        }
    }

    /// <summary>Completes the record and, when the run resolved a document, updates the ledger (PRD §05)
    /// and stamps the run that preceded it on the record.</summary>
    private void Finish(string executionId, ScriptExecutionOutcome outcome, string clientId, string? label)
    {
        var now = _now();
        CompleteExecution.Apply(_executionManager, executionId, now, outcome);
        if (outcome.DocumentId.Length == 0)
        {
            return;
        }

        var record = _executionManager.Poll(executionId);
        var previous = _ledger.Record(outcome.DocumentId, new LastRun
        {
            ExecutionId = executionId,
            AgentClientId = clientId,
            FinishedAt = now.ToString("o"),
            Status = record is null ? "" : ExecutionResultMessage.ToWireStatus(record.Status),
            Label = label,
            ChangedDocument = outcome.ChangedDocument,
            Tick = _executor.Clock.Next(),
        });
        record?.SetPreviousRun(previous);
        try { LedgerChanged?.Invoke(); } catch (Exception ex) { _log("ledger listener failed: " + ex.Message); }
    }

    /// <summary>The undo/redo tools' wire method (PRD §07): execution_id (server-minted), direction,
    /// confirm, document_id, timeout_ms. An undo IS an execution for the busy gate -- it goes through
    /// ExecutionManager.Start like a script, so a script arriving mid-undo is busy pointing at the undo
    /// and vice versa -- and its record is pollable. The work runs on the main thread via the launcher.</summary>
    private async Task<string> HandleUndoRedoAsync(JsonRpcRequest request)
    {
        string executionId, directionText, documentId;
        bool confirm;
        long timeoutMs;
        try
        {
            executionId = request.GetRequiredString("execution_id");
            directionText = request.GetRequiredString("direction");
            confirm = request.GetOptionalBool("confirm", false);
            documentId = request.GetOptionalString("document_id") ?? "";
            timeoutMs = request.GetOptionalInt64("timeout_ms", UndoDefaultTimeoutMs);
        }
        catch (JsonRpcParamException ex)
        {
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, ex.Diagnostic);
        }

        if (directionText is not ("undo" or "redo"))
        {
            var bad = DiagnosticRecord.Create(DiagnosticSeverity.Error, "invalid-params", DiagnosticSource.Execution,
                $"direction must be \"undo\" or \"redo\", got \"{directionText}\".", null, new[] { "Pass direction: \"undo\" or \"redo\"." });
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, bad.Message, bad);
        }

        var direction = directionText == "undo" ? UndoRedoExecutor.Direction.Undo : UndoRedoExecutor.Direction.Redo;
        timeoutMs = Math.Clamp(timeoutMs, 0, UndoMaxTimeoutMs);

        ExecuteOutcome started;
        try
        {
            // maxDuration comfortably past the wait: CheckMaxDuration must never cancel a live undo.
            started = _executionManager.Start(executionId, $"<{directionText}>", timeoutMs + 30_000, _now());
        }
        catch (ArgumentException ex)
        {
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, UnknownExecution(executionId));
        }

        switch (started.Kind)
        {
            case ExecuteOutcomeKind.Busy:
                return ExecutionResultMessage.Busy(request.Id, started.Record!.ExecutionId);
            case ExecuteOutcomeKind.InstanceUnrecoverable:
                return ExecutionResultMessage.FromInstanceUnrecoverable(request.Id, started.Diagnostic!);
        }

        _launcher.Post(() =>
        {
            if (_executionManager.MarkRunning(executionId, _now()) is not null)
            {
                return; // cancelled while queued
            }

            UndoRedoExecutor.Outcome outcome;
            try
            {
                outcome = _undoRedo.Execute(direction, documentId, confirm, executionId, _now());
            }
            catch (Exception ex)
            {
                outcome = new UndoRedoExecutor.Outcome
                {
                    Error = DiagnosticRecord.Create(DiagnosticSeverity.Error, "undo-failed", DiagnosticSource.Execution,
                        $"{directionText} threw {ex.GetType().Name}: {ex.Message}", new Dictionary<string, object?> { ["exception_type"] = ex.GetType().FullName }, null),
                };
            }

            if (outcome.Error is not null)
            {
                _executionManager.CompleteError(executionId, _now(), outcome.Error, null, outcome.Notices);
            }
            else
            {
                _executionManager.CompleteSuccess(executionId, _now(), null, null, outcome.Notices, null, outcome.Mutations);
            }
        });

        return await WaitAsync(request.Id, executionId, timeoutMs).ConfigureAwait(false);
    }

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
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, ex.Diagnostic);
        }

        return await WaitAsync(request.Id, executionId, timeoutMs).ConfigureAwait(false);
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
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, ex.Diagnostic);
        }

        var outcome = _executionManager.RequestCancellation(executionId, _now());
        if (outcome == CancellationRequestOutcome.NotFound)
        {
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, $"execution_id '{executionId}' is not known to this bridge.", UnknownExecution(executionId));
        }

        var record = _executionManager.Poll(executionId);
        return record is not null
            ? ExecutionResultMessage.FromRecord(request.Id, record)
            : JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InternalError, $"execution_id '{executionId}' vanished mid-cancellation.", UnknownExecution(executionId));
    }

    private static string UnknownMethod(JsonRpcRequest request)
    {
        var rec = DiagnosticRecord.Create(DiagnosticSeverity.Error, "unknown-method", DiagnosticSource.Connection,
            $"unknown method '{request.Method}'",
            new Dictionary<string, object?> { ["method"] = request.Method, ["supported_methods"] = SupportedMethods },
            new[] { "Call one of: " + string.Join(", ", SupportedMethods) + "." });
        return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.MethodNotFound, rec.Message, rec);
    }

    private static DiagnosticRecord UnknownExecution(string executionId) => DiagnosticRecord.Create(
        DiagnosticSeverity.Error, "unknown-execution-id", DiagnosticSource.Execution,
        $"execution_id '{executionId}' is not known to this bridge (never started, or evicted from the ring buffer).",
        new Dictionary<string, object?> { ["execution_id"] = executionId },
        new[] { "Start a new execution with execute_script." });
}
