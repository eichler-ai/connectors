using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MCPBridge.Core.Diagnostics;
using MCPBridge.Core.Discovery;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Protocol;
using MCPBridge.Core.Workspace;
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

    // Independent PR review finding: HandlePollExecutionAsync's wait loop checked its deadline against
    // the injectable _now() but always slept via the real, non-injectable Task.Delay -- a future test
    // that freezes _now() to exercise the deadline logic deterministically would spin real-time forever,
    // since _now() would never reach the deadline. _delay defaults to the real Task.Delay in production;
    // tests can substitute an instant no-op alongside a frozen/steppable _now.
    private readonly Func<TimeSpan, Task> _delay;

    // PRD §07 v1 non-framework-dialog fallback: null (the default) means the feature is off -- matches
    // this codebase's convention for optional add-in features (see CreateStatusRibbonButton's
    // best-effort registration). Production wiring (BridgeHost) passes a real Win32WindowInventory.
    private readonly IWindowInventory? _windowInventory;

    // PRD §08: discovery (list_functions/search_functions/describe_function) is pure reflection with no
    // Document/UIApplication dependency, so it deliberately does NOT go through ExecutionManager/
    // ExternalEventBridge at all -- served synchronously on the connection thread, answerable even while a
    // script is mid-execution on the UI thread. null (the default) means the feature is off, matching this
    // codebase's optional-feature convention (see _windowInventory above); production wiring (BridgeHost)
    // passes a real DiscoveryService built from Revit's already-loaded assemblies.
    private readonly DiscoveryService? _discoveryService;

    // Static for this process's lifetime (PRD §09: tmp/<instance-id>/ scopes scratch space per
    // instance sharing a workspace) -- safe to accept once at construction rather than per-call.
    private readonly string _instanceId;

    public RequestDispatcher(
        ExecutionManager executionManager,
        ExternalEventBridge<ScriptExecutionOutcome> bridge,
        TransactionScriptExecutor scriptExecutor,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, Task>? delay = null,
        IWindowInventory? windowInventory = null,
        DiscoveryService? discoveryService = null,
        string? instanceId = null)
    {
        _executionManager = executionManager;
        _bridge = bridge;
        _scriptExecutor = scriptExecutor;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _windowInventory = windowInventory;
        _discoveryService = discoveryService;
        _instanceId = instanceId ?? "";
    }

    /// <summary>How often <see cref="HandlePollExecutionAsync"/> re-checks the record while waiting out timeout_ms.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public Task<string> DispatchAsync(JsonRpcRequest request) => request.Method switch
    {
        "execute_script" => HandleExecuteScriptAsync(request),
        "poll_execution" => HandlePollExecutionAsync(request),
        "cancel_execution" => Task.FromResult(HandleCancelExecution(request)),
        "list_functions" => Task.FromResult(HandleListFunctions(request)),
        "search_functions" => Task.FromResult(HandleSearchFunctions(request)),
        "describe_function" => Task.FromResult(HandleDescribeFunction(request)),
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
        bool overwriteOutputFiles;
        try
        {
            executionId = request.GetRequiredString("execution_id");
            script = request.GetRequiredString("script");
            maxDurationMs = request.GetOptionalInt64("max_duration_ms", DefaultMaxDurationMs);
            timeoutMs = request.GetOptionalInt64("timeout_ms", DefaultTimeoutMs);

            // PRD §09: applies uniformly across every file ScriptGlobals.Publish touches during this
            // run -- not a per-file override.
            overwriteOutputFiles = request.GetOptionalBool("overwrite_output_files", false);

            // KNOWN LIMITATION (second live-wiring review finding, still true after PRD §09's file-exchange
            // work): document_id is part of the wire contract (the broker always sends it) but is not read
            // or enforced here for ROUTING -- every script still runs against whatever
            // IUiApplicationAdapter.ActiveUiDocument happens to be (see RunScriptWorkItem below), regardless
            // of which document_id was requested. Real per-document routing needs Application.Documents
            // (only reachable via DocumentSnapshotHandler's raw-Revit-API path, not the
            // IUiApplicationAdapter seam RunScriptWorkItem goes through) plus a way to select/activate the
            // target document -- still deferred, per that file's own comment. Document identity IS now
            // read (document.DocumentId, backed by DocumentIdentity.ResolveCached's shared cache, PRD §09)
            // from whichever document actually ends up active, purely to build that document's exports/
            // workspace path for Publish -- not to select which document runs. Silently ignoring a routing
            // mismatch (rather than erroring) is a deliberate choice: single active-document Revit
            // instances (the common case) work correctly either way.
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

        var workTask = RunScriptWorkItemAsync(executionId, script, overwriteOutputFiles);

        // PRD §06: a script finishing inside timeout_ms returns the completed result inline; one that
        // doesn't returns the current {status, execution_id} instead of hanging the call. The timeout
        // timer is cancelled as soon as workTask wins the race (the common case -- most scripts finish
        // well under timeout_ms) so it doesn't sit running in the background for the rest of its duration.
        using var timeoutCts = new CancellationTokenSource();
        var timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(ClampTimeoutMs(timeoutMs)), timeoutCts.Token);
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
        if (record is null)
        {
            return ExecutionResultMessage.Busy(request.Id, executionId); // defensive fallback; should be unreachable
        }

        // PRD §07 v1 fallback: this call's own local timeout_ms elapsed while the execution is still
        // Pending/Running (as opposed to workTask winning the race above) -- with DialogBoxShowing
        // installed, a framework dialog never blocks this long, so a genuine local timeout here means
        // something else (a non-framework dialog, a slow API call, a real hang) is holding the UI
        // thread. Diagnosis only, never persisted onto the record itself.
        var extraNotices = first != workTask && !record.Status.IsTerminal() ? BuildWindowInventoryNotices() : null;
        return ExecutionResultMessage.FromRecord(request.Id, record, extraNotices);
    }

    /// <summary>
    /// PRD §07 v1: enumerates top-level windows owned by this Revit process and wraps them into a
    /// single diagnostic notice, for a caller to attach to one specific poll/execute response.
    /// Deliberately does not mutate ExecutionRecord -- this is ephemeral, point-in-time diagnostic
    /// data, not part of the execution's permanent history. Best-effort: any failure (including
    /// _windowInventory being unset) degrades to no extra notice, never to a failed response.
    /// </summary>
    private IReadOnlyList<DiagnosticRecord>? BuildWindowInventoryNotices()
    {
        if (_windowInventory is null)
        {
            return null;
        }

        IReadOnlyList<WindowInfo> windows;
        try
        {
            windows = _windowInventory.EnumerateOwnedTopLevelWindows();
        }
        catch
        {
            return null;
        }

        if (windows.Count == 0)
        {
            return null;
        }

        var detail = new Dictionary<string, object?>
        {
            ["windows"] = windows
                .Select(w => new Dictionary<string, object?>
                {
                    ["title"] = w.Title,
                    ["class_name"] = w.ClassName,
                    ["text"] = w.ChildText,
                })
                .ToArray(),
        };

        return new[]
        {
            DiagnosticRecord.Create(
                DiagnosticSeverity.Info,
                "window-inventory-timeout-fallback",
                DiagnosticSource.Dialogs,
                "poll/execute timed out while the execution was still pending/running; top-level windows " +
                "owned by this Revit process are listed in detail.windows for manual triage (PRD §07 v1 -- " +
                "diagnosis only, no automatic action).",
                detail: detail,
                remedy: new[] { "Check Revit's screen for a modal dialog and dismiss it manually." }),
        };
    }

    private Task RunScriptWorkItemAsync(string executionId, string scriptText, bool overwriteOutputFiles)
    {
        var runTask = _bridge.RunAsync(executionId, uiApplication => RunScriptWorkItem(executionId, scriptText, overwriteOutputFiles, uiApplication));

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

    private ScriptExecutionOutcome RunScriptWorkItem(string executionId, string scriptText, bool overwriteOutputFiles, IUiApplicationAdapter uiApplication)
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

        // PRD §09: document.DocumentId is used here purely to build THIS document's imports/exports
        // workspace paths for Publish -- not to select/route which document the script runs against
        // (that's still whatever ActiveUiDocument already is, per the KNOWN LIMITATION comment above).
        // Independent PR review finding: document.DocumentId (not a fresh DocumentIdentity.Resolve
        // call here) is what makes this stable across calls -- see DocumentIdentity.ResolveCached's
        // own doc comment for why resolving fresh on every execution was wrong.
        var workspacePaths = WorkspacePaths.Local(document.DocumentId, _instanceId);
        workspacePaths.EnsureDirectoriesExist();

        // .GetAwaiter().GetResult() is deadlock-safe here only because RoslynScriptRunner rejects any
        // script containing its own top-level `await` before compiling it -- see
        // ExternalEventBridge<TResult>'s own doc comment and RoslynScriptRunner.RejectTopLevelAwait.
        var outcome = _scriptExecutor
            .ExecuteAsync(document, uiApplication, uiDocument, scriptText, cancellationToken, workspacePaths.Exports, workspacePaths.Imports, overwriteOutputFiles)
            .GetAwaiter().GetResult();

        if (outcome.WasCancelled)
        {
            _executionManager.CompleteCancelled(executionId, _now(), outcome.StdOut, outcome.Notices, outcome.Files);
        }
        else if (outcome.Success)
        {
            _executionManager.CompleteSuccess(executionId, _now(), outcome.ReturnValue, outcome.StdOut, outcome.Notices, outcome.Files);
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
            _executionManager.CompleteError(executionId, _now(), diagnostic, outcome.StdOut, outcome.Notices, outcome.Files);
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

        var deadline = _now().AddMilliseconds(ClampTimeoutMs(timeoutMs));
        while (true)
        {
            var record = _executionManager.Poll(executionId);
            if (record is null)
            {
                return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, $"execution_id '{executionId}' is not known to this add-in instance.", UnknownExecutionDiagnostic(executionId));
            }

            if (record.Status.IsTerminal())
            {
                return ExecutionResultMessage.FromRecord(request.Id, record);
            }

            if (_now() >= deadline)
            {
                return ExecutionResultMessage.FromRecord(request.Id, record, BuildWindowInventoryNotices());
            }

            await _delay(PollInterval).ConfigureAwait(false);
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
        // at the exact moment of the transition it describes, so there's no such window for THIS check.
        //
        // Second independent PR review finding: passing executionId to Abandon() (rather than an
        // identity-blind Abandon()) closes a separate, real window -- DispatchAsync's calls are
        // fire-and-continue (concurrent, not serialized; see the read loop's own comment), so a DIFFERENT
        // in-flight request can free ExecutionManager's slot and queue its own new RunAsync work item
        // between this line's RequestCancellation call and the Abandon() call below, with no lock held
        // across the two. An identity-blind Abandon() would fault that unrelated request's legitimately-
        // queued work item; the executionId-scoped compare-and-clear in ExternalEventBridge.Abandon()
        // makes this a no-op instead when that happens.
        if (outcome == CancellationRequestOutcome.AcknowledgedWasPending)
        {
            _bridge.Abandon(executionId);
        }

        var record = _executionManager.Poll(executionId);

        return record is not null
            ? ExecutionResultMessage.FromRecord(request.Id, record)
            : JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InternalError, $"execution_id '{executionId}' vanished mid-cancellation.", null);
    }

    /// <summary>
    /// PRD §08: list_functions/search_functions/describe_function are dispatched directly here -- no
    /// ExecutionManager/ExternalEventBridge/_bridge involvement whatsoever, since reflection never touches
    /// Document/UIApplication and must stay answerable even mid-script-execution. instance_id (present on
    /// every discovery request per the wire contract) is deliberately not read/validated here -- routing
    /// to this specific add-in instance already happened broker-side before this request ever arrived.
    /// </summary>
    private string HandleListFunctions(JsonRpcRequest request)
    {
        if (_discoveryService is null)
        {
            return DiscoveryUnavailable(request.Id);
        }

        try
        {
            var namespaceFilter = request.GetOptionalString("namespace");
            var typeFilter = request.GetOptionalString("type_name");
            var cursor = request.GetOptionalString("cursor");
            var pageSize = ClampPageSize(request.GetOptionalInt32("page_size", DefaultListFunctionsPageSize));

            var result = _discoveryService.ListFunctions(namespaceFilter, typeFilter, cursor, pageSize);
            return DiscoveryResultMessage.ListFunctions(request.Id, result);
        }
        catch (JsonRpcParamException ex)
        {
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, null);
        }
        catch (Exception ex)
        {
            return DiscoveryUnexpectedError(request.Id, "list_functions", ex);
        }
    }

    private string HandleSearchFunctions(JsonRpcRequest request)
    {
        if (_discoveryService is null)
        {
            return DiscoveryUnavailable(request.Id);
        }

        try
        {
            var query = request.GetRequiredString("query");
            var namespaceFilter = request.GetOptionalString("namespace");
            var cursor = request.GetOptionalString("cursor");
            var topN = ClampPageSize(request.GetOptionalInt32("top_n", DefaultSearchFunctionsTopN));

            var result = _discoveryService.SearchFunctions(query, namespaceFilter, cursor, topN);
            return DiscoveryResultMessage.SearchFunctions(request.Id, result);
        }
        catch (JsonRpcParamException ex)
        {
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, null);
        }
        catch (Exception ex)
        {
            return DiscoveryUnexpectedError(request.Id, "search_functions", ex);
        }
    }

    private string HandleDescribeFunction(JsonRpcRequest request)
    {
        if (_discoveryService is null)
        {
            return DiscoveryUnavailable(request.Id);
        }

        try
        {
            var member = request.GetRequiredString("member");
            var overloadIndex = request.GetOptionalInt32("overload_index", int.MinValue);
            var memberId = request.GetOptionalString("member_id");

            var result = _discoveryService.DescribeFunction(member, overloadIndex == int.MinValue ? null : overloadIndex, memberId);
            return DiscoveryResultMessage.DescribeFunction(request.Id, result);
        }
        catch (JsonRpcParamException ex)
        {
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, null);
        }
        catch (DiscoveryMemberNotFoundException ex)
        {
            var diagnostic = DiagnosticRecord.Create(
                DiagnosticSeverity.Error,
                "discovery-member-not-found",
                DiagnosticSource.Discovery,
                ex.Message,
                detail: new Dictionary<string, object?> { ["member"] = request.GetOptionalString("member") },
                remedy: new[] { "Use list_functions or search_functions to find the correct namespace/type/member name." });
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, diagnostic);
        }
        catch (Exception ex)
        {
            return DiscoveryUnexpectedError(request.Id, "describe_function", ex);
        }
    }

    /// <summary>
    /// Review finding (H2): DiscoveryService's reflection can throw for reasons beyond the two typed
    /// exceptions above (a hostile/unresolvable type in a C++/CLI interop assembly, etc.) -- this runs on
    /// the add-in's background connection thread (PRD §08's execution-locus decision), so an uncaught
    /// exception here would take down that connection entirely rather than surfacing as one failed call.
    /// </summary>
    private static string DiscoveryUnexpectedError(System.Text.Json.JsonElement id, string method, Exception ex)
    {
        var diagnostic = DiagnosticRecord.Create(
            DiagnosticSeverity.Error,
            "discovery-unexpected-error",
            DiagnosticSource.Discovery,
            $"{method} failed unexpectedly: {ex.Message}",
            detail: new Dictionary<string, object?> { ["method"] = method },
            remedy: null);
        return JsonRpcErrorMessage.ToJson(id, JsonRpcErrorCode.InternalError, diagnostic.Message, diagnostic);
    }

    /// <summary>
    /// Review finding (H3): page_size/top_n were passed straight through unvalidated. A caller-supplied
    /// value &lt;= 0 makes list_functions/search_functions return an empty page whose next_cursor equals
    /// the cursor just supplied -- an agent paginating in a loop never terminates. A caller-supplied huge
    /// value returns the entire scoped/matched surface in one response, blowing straight past the
    /// ~25,000-token MCP output ceiling PRD §08 explicitly designs pagination around. Same clamping
    /// pattern as <see cref="ClampTimeoutMs"/>.
    /// </summary>
    private static int ClampPageSize(int pageSize) => Math.Clamp(pageSize, 1, 500);

    private static string DiscoveryUnavailable(System.Text.Json.JsonElement id) => JsonRpcErrorMessage.ToJson(
        id,
        JsonRpcErrorCode.InternalError,
        "discovery is not available on this connection (no DiscoveryService wired up).",
        DiagnosticRecord.Create(
            DiagnosticSeverity.Error,
            "discovery-unavailable",
            DiagnosticSource.Discovery,
            "discovery is not available on this connection (no DiscoveryService wired up).",
            detail: null,
            remedy: null));

    private const int DefaultListFunctionsPageSize = 50;
    private const int DefaultSearchFunctionsTopN = 20;

    /// <summary>
    /// Clamps a caller-supplied timeout_ms to [0, DefaultMaxDurationMs] -- shared by both
    /// HandleExecuteScriptAsync and HandlePollExecutionAsync. Independent PR review finding: an unbounded
    /// value (e.g. accidentally passed in the wrong unit) could push a deadline computation past
    /// DateTimeOffset's representable range (throwing) or TimeSpan.FromMilliseconds past its own range
    /// (also throwing), or just hold a dispatch loop waiting far longer than anything in this system ever
    /// legitimately runs for. The first version of this fix only applied the clamp to poll_execution;
    /// execute_script's own wait had the identical unclamped Task.Delay(TimeSpan.FromMilliseconds(...))
    /// call. DefaultMaxDurationMs (PRD §06's own "default generous, e.g. 10 minutes" ceiling for
    /// execute_script) is already the longest a script is expected to run, so it doubles as a sane upper
    /// bound for how long either call should ever be worth waiting.
    /// </summary>
    private static long ClampTimeoutMs(long timeoutMs) => Math.Clamp(timeoutMs, 0, DefaultMaxDurationMs);

    private static DiagnosticRecord UnknownExecutionDiagnostic(string executionId) => DiagnosticRecord.Create(
        DiagnosticSeverity.Error,
        "unknown_execution_id",
        DiagnosticSource.Execution,
        $"execution_id '{executionId}' is not known to this add-in instance (never started, or evicted from the ring buffer).",
        detail: new Dictionary<string, object?> { ["execution_id"] = executionId },
        remedy: new[] { "Start a new execution with execute_script." });
}
