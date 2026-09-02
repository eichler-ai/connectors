using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Scripting;
using Eichler.Connectors.Revit;
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
/// Encodes the live-wiring contract's hard requirements (originally recorded as a TODO in
/// BridgeHost.cs, long since implemented and the TODO removed -- restated here as the requirements
/// themselves rather than a pointer to a landmark that no longer exists):
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

    // #136: the §07 window inventory (BuildWindowInventoryNotices) reads window text via a blocking
    // Win32 SendMessageTimeout against the very UI thread it is inspecting, so under a long-running
    // script it consumes real wall-clock. It is diagnosis-only and must NEVER delay the pending response
    // it annotates past the broker's wire budget -- doing so WAS #136: the diagnostic timing out the very
    // wire call it exists to explain, intermittently, once a session had accumulated enough top-level
    // windows to spend its whole budget. So the timeout-branch response waits for the inventory only
    // within the slice of the wire budget it can spare, and returns the plain pending status without it
    // when too little remains.
    //
    // WireResponseBufferMs mirrors the broker's own grace on top of timeout_ms -- callWire in
    // mcp-server/internal/execution/execution.go bounds the round trip to `timeout_ms + 5s`. The two are
    // hand-synced across the wire (per CONVENTIONS.md's "mirrored on both ends" rule); this is the add-in
    // staying safely INSIDE that budget, so if the broker's grace ever shrinks, this constant must follow.
    // Deliberately conservative rather than exact -- underestimating the real remaining budget only makes
    // the diagnostic bail sooner, never the wire call fail.
    private const long WireResponseBufferMs = 5_000;

    // Reserve of the wire buffer kept for serialising and writing the response after the inventory runs
    // (the continuation hop and the framed NDJSON write, both off a possibly-contended thread pool).
    private const long InventoryResponseWriteReserveMs = 1_500;

    // Hard ceiling on how long the response will ever wait for the inventory, independent of budget math.
    private const long InventoryHardCapMs = 1_500;

    // Below this, running the inventory at all is not worth the risk to the response -- skip it.
    private const long InventoryMinBudgetMs = 250;

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

    internal RequestDispatcher(
        ExecutionManager executionManager,
        ExternalEventBridge<ScriptExecutionOutcome> bridge,
        TransactionScriptExecutor scriptExecutor,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, Task>? delay = null,
        IWindowInventory? windowInventory = null,
        DiscoveryService? discoveryService = null,
        string? instanceId = null,
        Action<string>? auditTrailTrace = null)
    {
        _executionManager = executionManager;
        _bridge = bridge;
        _scriptExecutor = scriptExecutor;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _windowInventory = windowInventory;
        _discoveryService = discoveryService;
        _instanceId = instanceId ?? "";
        _auditTrailTrace = auditTrailTrace;
    }

    // Where an audit-trail failure's §01-style trace goes (the AddIn passes the connection-log
    // writer); null (tests, or nothing wired) means the failure is silently swallowed, which the
    // audit trail's never-affect-the-run contract permits.
    private readonly Action<string>? _auditTrailTrace;

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
        _ => Task.FromResult(UnknownMethod(request)),
    };

    /// <summary>
    /// The method-not-found response. Carries a §01 record for the same reason every param error now
    /// does (issue #69): without one the broker's fromRPCError stamps it `add-in-error`, which tells an
    /// agent only that something went wrong on the other side.
    /// </summary>
    private static string UnknownMethod(JsonRpcRequest request)
    {
        var diagnostic = DiagnosticRecord.Create(
            DiagnosticSeverity.Error,
            "unknown-method",
            DiagnosticSource.Connection,
            $"unknown method '{request.Method}'",
            detail: new Dictionary<string, object?>
            {
                ["method"] = request.Method,
                ["supported_methods"] = SupportedMethods,
            },
            remedy: new[] { "Call one of: " + string.Join(", ", SupportedMethods) + "." });
        return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.MethodNotFound, $"unknown method '{request.Method}'", diagnostic);
    }

    /// <summary>
    /// Every method <see cref="DispatchAsync"/> routes. A C# switch over strings cannot be enumerated at
    /// runtime, so this is a hand-maintained mirror of it -- state that plainly rather than implying it
    /// is derived. `EveryMethodInSupportedMethodsIsActuallyRouted` catches the deletion direction (a name
    /// listed here that no longer routes); the addition direction (a new case not added here) is NOT
    /// caught by anything, and the mitigation is that the only cost is an incomplete remedy list.
    /// </summary>
    private static readonly string[] SupportedMethods =
    {
        "execute_script", "poll_execution", "cancel_execution",
        "list_functions", "search_functions", "describe_function",
    };

    private async Task<string> HandleExecuteScriptAsync(JsonRpcRequest request)
    {
        // #136: real wall-clock spent in this handler, so the window-inventory diagnostic on the timeout
        // branch can be bounded to the slice of the broker's wire budget it can still spare
        // (BuildWindowInventoryNoticesWithinWireBudget).
        var handlerSw = System.Diagnostics.Stopwatch.StartNew();
        string executionId;
        string script;
        string documentId;
        long maxDurationMs;
        long timeoutMs;
        bool overwriteOutputFiles;
        bool confirmLifecycleActions;
        try
        {
            executionId = request.GetRequiredString("execution_id");
            script = request.GetRequiredString("script");
            documentId = request.GetOptionalString("document_id") ?? "";
            maxDurationMs = request.GetOptionalInt64("max_duration_ms", DefaultMaxDurationMs);
            timeoutMs = request.GetOptionalInt64("timeout_ms", DefaultTimeoutMs);

            // PRD §09: applies uniformly across every file ScriptGlobals.Publish touches during this
            // run -- not a per-file override.
            overwriteOutputFiles = request.GetOptionalBool("overwrite_output_files", false);

            // PRD §14: opt-in for the confirmation-gated lifecycle members (Document.Close/Save/SaveAs/
            // SynchronizeWithCentral/Print, WorksharingUtils.RelinquishOwnership). Per-REQUEST, not
            // per-script: the same script text may arrive once without it and again with it, so it is
            // read here on every call and forwarded down to the run, never folded into the compilation
            // cache. Defaults to false -- an agent that never heard of the flag cannot trip these members
            // by accident, which is the entire point of gating them.
            confirmLifecycleActions = request.GetOptionalBool("confirm_lifecycle_actions", false);

            // document_id now ROUTES (v1 integrated review; this closed the long-standing
            // accepted-but-ignored gap CONVENTIONS.md's advertised-but-unimplemented clause was written
            // about). Empty/omitted keeps the active-document behavior every existing caller relies on;
            // a non-empty id is resolved against the §09 identities of every open document in
            // RunScriptWorkItem below -- running against that document (with the workspace paths
            // following it), erroring loudly with a candidates list when nothing matches, and never
            // silently falling back to a different document than the one addressed.
        }
        catch (JsonRpcParamException ex)
        {
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, ex.Diagnostic);
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
            //
            // The record is synthesised here rather than carried on the exception (the shape issue #69
            // gave JsonRpcParamException): this is a plain ArgumentException from ExecutionManager, whose
            // own contract is an ordinary .NET argument guard, and giving it a wire-diagnostic field would
            // push protocol concerns into a module that has none. It is included in this change anyway
            // because it is the SAME defect one line away -- an InvalidParams with a bare message, from
            // the same handler -- and leaving it would just relocate the inconsistency the issue is about.
            var startDiagnostic = DiagnosticRecord.Create(
                DiagnosticSeverity.Error,
                "invalid-execution-id",
                DiagnosticSource.Execution,
                ex.Message,
                detail: new Dictionary<string, object?> { ["param"] = "execution_id", ["execution_id"] = executionId },
                remedy: new[] { "Mint a fresh, unique execution_id for each execute_script call and echo it back unchanged on poll_execution/cancel_execution." });
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, startDiagnostic);
        }

        switch (outcome.Kind)
        {
            case ExecuteOutcomeKind.Busy:
                return ExecutionResultMessage.Busy(request.Id, outcome.Record!.ExecutionId);
            case ExecuteOutcomeKind.InstanceUnrecoverable:
                return ExecutionResultMessage.FromInstanceUnrecoverable(request.Id, outcome.Diagnostic!);
        }

        // #67: compile + denylist-check on THIS (connection) thread, before raising the ExternalEvent. A
        // compile-time rejection -- bad C#, a denylisted member, an unconfirmed lifecycle call -- is a pure
        // property of the script text, needing nothing from Revit's UI thread. Rejecting it here makes the
        // rejection immediate and DETERMINISTIC: it can no longer queue behind a congested UI thread and be
        // reported as `running` with the instance left busy for a script that never runs (the exact #67
        // symptom). On success the compile is left warm in the cache, so the UI-thread run reuses it with no
        // recompile. The pre-flight is synchronous, so the ExternalEvent is still raised before the first
        // await below -- callers that queue work against the raise (and the harness's OnExecute) are unaffected.
        //
        // #136 self-consistency: this puts a compile on the response path, so it must not risk the broker's
        // `timeout_ms + 5s` wire budget. It runs ONLY once the pipeline is warm (WarmupCompile has JITed
        // Roslyn and emitted a first script at startup) -- after which any realistic agent script compiles in
        // ~ms. A script that arrives before warmup completes simply skips the pre-flight and takes the old
        // work-item path (compile on the UI thread, `running` at timeout_ms), so a cold compile is never on
        // the response path -- which is exactly the "racing an unfinished warmup" window that would otherwise
        // blow the budget.
        if (_scriptExecutor.IsWarm)
        {
            var rejection = _scriptExecutor.TryPreflight(script, confirmLifecycleActions);
            if (rejection is not null)
            {
                // On-disk trace of the rejected attempt (§01/§09 spirit): a compile-time rejection now settles
                // before the §09 document-scoped audit trail runs, so the connection log is where a refused
                // attempt -- most importantly a denylist violation, a security-guard trip -- leaves its mark.
                _auditTrailTrace?.Invoke(
                    $"[#67] script rejected pre-flight (execution_id \"{executionId}\"): " +
                    $"{rejection.Exception?.GetType().Name} — {rejection.Exception?.Message}");
                CompleteExecutionAsError(executionId, rejection);
                return ExecutionResultMessage.FromRecord(request.Id, _executionManager.Poll(executionId)!);
            }
        }

        var workTask = RunScriptWorkItemAsync(executionId, script, documentId, overwriteOutputFiles, confirmLifecycleActions);

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
        IReadOnlyList<DiagnosticRecord>? extraNotices = null;
        if (first != workTask && !record.Status.IsTerminal())
        {
            extraNotices = await BuildWindowInventoryNoticesWithinWireBudget(handlerSw, timeoutMs).ConfigureAwait(false);
        }
        return ExecutionResultMessage.FromRecord(request.Id, record, extraNotices);
    }

    /// <summary>
    /// #136: how many ms the pending-response path may wait for the window inventory, given how much of
    /// this call's broker wire budget (ClampTimeoutMs(timeout_ms) + <see cref="WireResponseBufferMs"/>)
    /// the handler has already spent, after reserving <see cref="InventoryResponseWriteReserveMs"/> to
    /// serialise and write the response. Hard-ceilinged at <see cref="InventoryHardCapMs"/>. A result
    /// below <see cref="InventoryMinBudgetMs"/> means "skip the inventory" -- can be negative when the
    /// budget is already blown. Pure and static so it is unit-testable without a real clock.
    /// </summary>
    internal static long ComputeInventoryBudgetMs(long handlerElapsedMs, long timeoutMs)
    {
        // ClampTimeoutMs floors at 0, so this over-estimates the real budget only if the broker ever sent a
        // NEGATIVE timeout_ms -- which it does not: timeout_ms is a non-negative wait, and the broker's own
        // budget is `timeout_ms + 5s` off the same value. Every other input is safe: a huge timeout_ms is
        // clamped before it can inflate the budget, and a budget already blown yields a negative result,
        // which the caller reads as "skip". So the estimate is conservative wherever it matters.
        var wireBudgetMs = ClampTimeoutMs(timeoutMs) + WireResponseBufferMs;
        var remainingMs = wireBudgetMs - handlerElapsedMs - InventoryResponseWriteReserveMs;
        return Math.Min(InventoryHardCapMs, remainingMs);
    }

    /// <summary>
    /// #136: runs <see cref="BuildWindowInventoryNotices"/> but bounds how long the pending response
    /// will wait for it to the slice of the broker's wire budget still unspent, so the diagnostic can
    /// never blow the very wire call it annotates. When the budget wins, the response returns without the
    /// notice, and the inventory Task is left to finish on its own -- its blocking Win32 reads are already
    /// self-limited (Win32WindowInventory's own OverallBudgetMs) -- so that its true, uncapped elapsed
    /// gets logged for diagnosis (the very question #136 turned on: how long is the §07 pass really taking).
    /// </summary>
    private async Task<IReadOnlyList<DiagnosticRecord>?> BuildWindowInventoryNoticesWithinWireBudget(
        System.Diagnostics.Stopwatch handlerSw, long timeoutMs)
    {
        if (_windowInventory is null)
        {
            return null;
        }

        var budgetMs = ComputeInventoryBudgetMs(handlerSw.ElapsedMilliseconds, timeoutMs);
        if (budgetMs < InventoryMinBudgetMs)
        {
            _auditTrailTrace?.Invoke(
                $"[#136] window inventory skipped: only {budgetMs}ms of wire budget left for it " +
                $"(timeout_ms={timeoutMs}, handler_elapsed_ms={handlerSw.ElapsedMilliseconds})");
            return null;
        }

        // §07 v2 dismissals flow out through this side channel, not the (abandonable) return value: a
        // dismissal is an action already taken and MUST be reported even when the pass is abandoned below.
        // The target modal is dismissed early in the pass (it is topmost and pumps its own loop, so its
        // read is fast), so the snapshot taken after the race captures it in the common case.
        var dismissedSideChannel = new System.Collections.Concurrent.ConcurrentQueue<DismissedDialog>();

        // Real Task.Delay, not the injectable _delay: this bound protects a real wall-clock wire deadline,
        // so it must elapse in real time even under a test clock. (_delay stays the seam for the poll-loop
        // WAIT, which is logical time.)
        var inventorySw = System.Diagnostics.Stopwatch.StartNew();
        var inventoryTask = Task.Run(() => BuildWindowInventoryNotices(dismissedSideChannel.Enqueue));
        var winner = await Task.WhenAny(inventoryTask, Task.Delay(TimeSpan.FromMilliseconds(budgetMs))).ConfigureAwait(false);

        IReadOnlyList<DiagnosticRecord>? inventoryNotices;
        if (winner == inventoryTask)
        {
            inventoryNotices = await inventoryTask.ConfigureAwait(false);
        }
        else
        {
            inventoryNotices = null;

            // Budget won: send the pending status without the diagnostic rather than hold the wire call.
            // Report the drop now (§01 observability-over-silence) so a missing window inventory on a timeout
            // is a stated outcome, not an unexplained gap -- it means a script is holding the UI thread long
            // enough that reading the windows' text would have risked the response's own wire budget.
            _auditTrailTrace?.Invoke(
                $"[#136] window inventory dropped from this response after its {budgetMs}ms wire-budget slice " +
                "elapsed (a script is holding the UI thread); the pending status is returned without it. Its " +
                "true elapsed will follow when the abandoned pass finishes.");

            // Observe the abandoned Task so a fault never surfaces as an unobserved-task exception, and log
            // its true uncapped elapsed on completion either way -- that number is what says whether the cap
            // is sized right (#136). The TOTAL dismissal count is logged here too: if it exceeds what the
            // response below reported (a dismissal that landed after the response's snapshot), that late
            // auto-dismiss is at least never fully silent (§01), it is in the log.
            _ = inventoryTask.ContinueWith(
                t =>
                {
                    inventorySw.Stop();
                    var faulted = t.IsFaulted ? $"; faulted: {t.Exception?.GetBaseException().Message}" : "";
                    _auditTrailTrace?.Invoke(
                        $"[#136] abandoned window inventory finished after {inventorySw.ElapsedMilliseconds}ms " +
                        $"(budget was {budgetMs}ms); {dismissedSideChannel.Count} dialog(s) auto-dismissed in total{faulted}");
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        // A dismissal is an action already taken, so report it whether or not the diagnostic inventory
        // survived the cap: merge the §07 v2 auto-dismiss notice (from the side channel) with the v1
        // inventory notice (present only if the pass completed within budget).
        var dismissalNotice = BuildDialogAutoDismissedNotice(dismissedSideChannel.ToArray());
        if (dismissalNotice is null)
        {
            return inventoryNotices;
        }

        var merged = new List<DiagnosticRecord> { dismissalNotice };
        if (inventoryNotices is not null)
        {
            merged.AddRange(inventoryNotices);
        }

        return merged;
    }

    /// <summary>
    /// PRD §07 v1: enumerates top-level windows owned by this Revit process and wraps them into a
    /// single diagnostic notice, for a caller to attach to one specific poll/execute response.
    /// Deliberately does not mutate ExecutionRecord -- this is ephemeral, point-in-time diagnostic
    /// data, not part of the execution's permanent history. Best-effort: any failure (including
    /// _windowInventory being unset) degrades to no extra notice, never to a failed response.
    /// </summary>
    private IReadOnlyList<DiagnosticRecord>? BuildWindowInventoryNotices(Action<DismissedDialog> onDismissed)
    {
        if (_windowInventory is null)
        {
            return null;
        }

        WindowInventorySnapshot snapshot;
        try
        {
            // §07 v2: the enumeration also auto-dismisses allowlisted raw Win32 (#32770) dialogs the
            // Revit-framework suppressor cannot see. The allowlist DECISION lives in Core
            // (DialogAutoDismissPolicy) and is passed in as a pure predicate; the ACTION (WM_CLOSE) stays
            // in the adapter. Dismissals are NOT reported from here: this method's return value is subject
            // to #138's wire-budget abandon, and a dismissal (an action already taken) must be reported
            // regardless -- so it flows out through onDismissed, which the caller captures even on abandon.
            snapshot = _windowInventory.EnumerateOwnedTopLevelWindows(DialogAutoDismissPolicy.ShouldDismiss, onDismissed);
        }
        catch
        {
            return null;
        }

        if (snapshot.Windows.Count == 0 && !snapshot.Truncated)
        {
            return null;
        }

        var detail = new Dictionary<string, object?>
        {
            ["windows"] = snapshot.Windows
                .Select(w => new Dictionary<string, object?>
                {
                    ["title"] = w.Title,
                    ["class_name"] = w.ClassName,
                    ["text"] = w.ChildText,
                })
                .ToArray(),
            // PRD §01 honesty (independent PR review finding): the enumeration runs under a hard
            // time budget precisely because the UI thread it inspects may not be pumping, so a
            // busy session can yield a PARTIAL inventory -- and a partial list presented as
            // complete would have a reader conclude a window doesn't exist when it merely wasn't
            // reached. Present on every notice (not only when true) so its absence is never
            // ambiguous with an older add-in that didn't report it.
            ["inventory_truncated"] = snapshot.Truncated,
        };

        var message = "poll/execute timed out while the execution was still pending/running; top-level windows " +
            "owned by this Revit process are listed in detail.windows for manual triage (PRD §07 v1 -- " +
            "diagnosis only, no automatic action).";
        if (snapshot.Truncated)
        {
            message += " NOTE: the inventory is INCOMPLETE -- enumeration or window-text reads exceeded " +
                "their time budget (a busy UI thread does not answer text queries), so windows or text " +
                "may be missing from this list.";
        }

        return new[]
        {
            DiagnosticRecord.Create(
                DiagnosticSeverity.Info,
                "window-inventory-timeout-fallback",
                DiagnosticSource.Dialogs,
                message,
                detail: detail,
                remedy: new[] { "Check Revit's screen for a modal dialog and dismiss it manually." }),
        };
    }

    /// <summary>
    /// PRD §07 v2: reports allowlisted raw Win32 (#32770) dialogs that this pass auto-dismissed
    /// (DialogAutoDismissPolicy + Win32WindowInventory's WM_CLOSE). §01 observability-over-silence: an
    /// action taken on the agent's behalf MUST be stated, never silent. Returns null when nothing was
    /// dismissed, so the default (no-match) behavior is unchanged.
    /// </summary>
    private static DiagnosticRecord? BuildDialogAutoDismissedNotice(IReadOnlyList<DismissedDialog> dismissed)
    {
        if (dismissed.Count == 0)
        {
            return null;
        }

        var named = string.Join(
            ", ",
            dismissed.Select(d => $"\"{d.Title}\" (class {d.ClassName})"));

        var message = $"Auto-dismissed {dismissed.Count} allowlisted dialog(s) on the agent's behalf per " +
            $"the PRD §07 auto-dismiss allowlist: {named}. These are known-benign, informational Win32 " +
            "dialogs that the Revit-framework dialog suppressor cannot see; each was closed with WM_CLOSE " +
            "(no button clicked, no \"do not show again\" set).";

        var detail = new Dictionary<string, object?>
        {
            ["dismissed"] = dismissed
                .Select(d => new Dictionary<string, object?>
                {
                    ["title"] = d.Title,
                    ["class_name"] = d.ClassName,
                })
                .ToArray(),
        };

        return DiagnosticRecord.Create(
            DiagnosticSeverity.Info,
            "dialog-auto-dismissed",
            DiagnosticSource.Dialogs,
            message,
            detail: detail,
            remedy: new[] { "None -- the dialog was informational and has been dismissed automatically." });
    }

    private Task RunScriptWorkItemAsync(string executionId, string scriptText, string requestedDocumentId, bool overwriteOutputFiles, bool confirmLifecycleActions)
    {
        var runTask = _bridge.RunAsync(
            executionId,
            uiApplication => RunScriptWorkItem(executionId, scriptText, requestedDocumentId, overwriteOutputFiles, confirmLifecycleActions, uiApplication));

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

    private ScriptExecutionOutcome RunScriptWorkItem(
        string executionId,
        string scriptText,
        string requestedDocumentId,
        bool overwriteOutputFiles,
        bool confirmLifecycleActions,
        IUiApplicationAdapter uiApplication)
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

        // ROUTING (PRD §05's {instance_id, document_id} addressing, implemented for real -- v1
        // integrated review). Empty/omitted document_id keeps the active document, today's behavior
        // for every existing caller. A non-empty id resolves against the §09 identities of every open
        // document: the active document is the fast path (and the only case with a genuine UIDocument
        // to expose); a match on any other open document routes there with UIDocument deliberately
        // null -- Revit has no UIDocument for a background document, and handing the active one's to a
        // script addressed elsewhere would be exactly the wrong-document hazard routing exists to end.
        // No match errors loudly with a candidates list, never a silent fallback (CONVENTIONS.md's
        // advertised-but-unimplemented clause -- this parameter is why it exists).
        var uiDocument = uiApplication.ActiveUiDocument;
        var document = uiDocument?.Document;
        if (requestedDocumentId.Length != 0 && document?.DocumentId != requestedDocumentId)
        {
            document = uiApplication.FindOpenDocument(requestedDocumentId);
            uiDocument = null;
            if (document is null)
            {
                var candidates = uiApplication.OpenDocuments;
                var diagnostic = DiagnosticRecord.Create(
                    DiagnosticSeverity.Error,
                    "document-not-found",
                    DiagnosticSource.Execution,
                    $"execution {executionId} could not run: no open document in this instance has document_id '{requestedDocumentId}'.",
                    detail: new Dictionary<string, object?>
                    {
                        ["execution_id"] = executionId,
                        ["requested_document_id"] = requestedDocumentId,
                        ["open_documents"] = candidates
                            .Select(c => new Dictionary<string, object?>
                            {
                                ["document_id"] = c.DocumentId,
                                ["title"] = c.Title,
                                ["active"] = c.IsActive,
                            })
                            .ToList(),
                    },
                    remedy: new[]
                    {
                        "Pick a document_id from open_documents in this error's detail (or call list_instances for the same list), then retry.",
                        "If the document was open a moment ago, it may have been closed or re-identified after a save -- list_instances reflects the current state.",
                    });
                _executionManager.CompleteError(executionId, _now(), diagnostic, stdOut: null);
                return ScriptExecutionOutcome.Failed(new InvalidOperationException(diagnostic.Message), "");
            }
        }

        if (document is null)
        {
            var diagnostic = DiagnosticRecord.Create(
                DiagnosticSeverity.Error,
                "no-active-document",
                DiagnosticSource.Execution,
                $"execution {executionId} could not run: this Revit instance has no active document.",
                detail: new Dictionary<string, object?> { ["execution_id"] = executionId },
                remedy: new[] { "Open a document in this Revit instance and retry, or address an open document explicitly by document_id." });
            _executionManager.CompleteError(executionId, _now(), diagnostic, stdOut: null);
            return ScriptExecutionOutcome.Failed(new InvalidOperationException(diagnostic.Message), "");
        }

        // PRD §09: the workspace paths follow the ROUTED document -- document here is whichever open
        // document the routing above selected (active by default, or the addressed one), so Publish
        // and imports/exports always land in the workspace of the document the script actually ran
        // against. Independent PR review finding: document.DocumentId (not a fresh
        // DocumentIdentity.Resolve call here) is what makes this stable across calls -- see
        // DocumentIdentity.ResolveCached's own doc comment for why resolving fresh per execution was
        // wrong.
        var workspacePaths = WorkspacePaths.Local(document.DocumentId, _instanceId);
        workspacePaths.EnsureDirectoriesExist();

        // .GetAwaiter().GetResult() is deadlock-safe here only because RoslynScriptRunner rejects any
        // script containing its own top-level `await` before compiling it -- see
        // ExternalEventBridge<TResult>'s own doc comment and RoslynScriptRunner.RejectTopLevelAwait.
        var outcome = _scriptExecutor
            .ExecuteAsync(
                document,
                uiApplication,
                uiDocument,
                scriptText,
                cancellationToken,
                // Named from here on: four optional arguments in a row, two of them adjacent bools, is
                // a swap this compiles straight through (overwriteOutputFiles and confirmLifecycleActions
                // are both bool, and reversing them would silently confirm lifecycle actions nobody asked
                // to confirm).
                exportsDirectoryPath: workspacePaths.Exports,
                importsDirectoryPath: workspacePaths.Imports,
                overwriteOutputFiles: overwriteOutputFiles,
                confirmLifecycleActions: confirmLifecycleActions)
            .GetAwaiter().GetResult();

        if (outcome.WasCancelled)
        {
            _executionManager.CompleteCancelled(executionId, _now(), outcome.StdOut, outcome.Notices, outcome.Files);
        }
        else if (outcome.Success)
        {
            _executionManager.CompleteSuccess(executionId, _now(), SafeFormatReturnValue(outcome.ReturnValue), outcome.StdOut, outcome.Notices, outcome.Files);
        }
        else
        {
            CompleteExecutionAsError(executionId, outcome);
        }

        // The §09 audit trail (issue #13): the verbatim script and a per-run NDJSON log land in the
        // ROUTED document's workspace after the outcome settles -- best-effort by hard contract
        // (Record never throws), so it can neither fail nor reorder anything above. Runs refused
        // before a document was resolved never reach here, which is deliberate -- see
        // ExecutionAuditTrail's own doc for why they leave no audit entry.
        ExecutionAuditTrail.Record(workspacePaths, executionId, scriptText, outcome, _now(), _auditTrailTrace);

        return outcome;
    }

    /// <summary>
    /// Formats a script's return value to its final display string HERE, on the UI thread, the moment
    /// the run completes -- the ring buffer then stores only the string (v1 integrated review; the
    /// in-code lead on issue #31's memory growth). Storing the raw object for the record's retention
    /// window (last ~50 entries / 10 minutes) had three failure modes: (1) a script returning an
    /// instance of a script-defined type -- `return new { ... }` is idiomatic agent code -- roots the
    /// emitted submission assembly via GetType(), so RoslynScriptRunner's collectible-ALC unload
    /// (PRD §06's memory-lifecycle design) silently could not reclaim exactly those runs until ring
    /// eviction; (2) a returned Document/Element pins its Revit wrapper across a document close --
    /// issue #31's create/write/close shape; (3) the old format-at-serialization call ran ToString()
    /// on the TCP thread, a Revit API call off the API context whenever the type overrides ToString.
    /// The try/catch is for the same reason the formatting moved: a script-defined ToString() is
    /// arbitrary code and must not turn a completed run into an unhandled UI-thread exception.
    ///
    /// <para>The formatting ITSELF lives in <see cref="ReturnValueFormatter"/> (issue #117), which
    /// replaced this method's original one-liner -- <c>value as string ?? value.ToString()</c> -- because
    /// that rendered `return levels.Select(l =&gt; new { l.Name, l.Elevation }).ToList()` as the collection's
    /// type name and gave a caller no way to tell that from data. That class owns the bounds (depth, node
    /// count, characters, cycles) which are what make reflecting over a script-controlled graph safe to do
    /// here, on the UI thread. This try/catch stays as the outermost guarantee: the formatter is written
    /// not to throw, and a bug in it still must not crash the UI thread.</para>
    /// </summary>
    private static string? SafeFormatReturnValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return ReturnValueFormatter.Format(value);
        }
        catch (Exception ex)
        {
            // Deliberately no ex.Message here (PR review finding): Message is virtual and
            // script-definable, so interpolating it would have the guard against arbitrary script
            // code calling arbitrary script code. GetType().Name cannot run script code.
            return $"<return value of type {value.GetType().FullName} -- formatting threw {ex.GetType().Name}>";
        }
    }

    /// <summary>
    /// Settles <paramref name="executionId"/>'s record as a terminal error from a failed
    /// <see cref="ScriptExecutionOutcome"/>, mapping the exception to its §01 code/remedy
    /// (<see cref="FailureCodeAndRemedy"/>). Shared by the UI-thread work item's failure branch and #67's
    /// connection-thread pre-flight rejection, so both report an invalid script identically -- the only
    /// difference is that the pre-flight path never raised the ExternalEvent.
    /// </summary>
    private void CompleteExecutionAsError(string executionId, ScriptExecutionOutcome outcome)
    {
        var (code, remedy) = FailureCodeAndRemedy(outcome.Exception);
        var diagnostic = DiagnosticRecord.Create(
            DiagnosticSeverity.Error,
            code,
            DiagnosticSource.Execution,
            outcome.Exception?.Message ?? $"execution {executionId} failed with no exception detail.",
            // exception_type: PRD §01 requires the wrapped exception's message AND type, and the
            // type genuinely disambiguates -- §14 records that Autodesk's and System's
            // InvalidOperationException share a short name, so a message alone can send a reader
            // to the wrong catch clause (v1 integrated review). Omitted, not null, when there is
            // no exception object at all.
            detail: outcome.Exception is null
                ? new Dictionary<string, object?> { ["execution_id"] = executionId }
                : new Dictionary<string, object?>
                {
                    ["execution_id"] = executionId,
                    ["exception_type"] = outcome.Exception.GetType().FullName,
                },
            remedy: remedy);
        _executionManager.CompleteError(executionId, _now(), diagnostic, outcome.StdOut, outcome.Notices, outcome.Files);
    }

    /// <summary>
    /// The PRD §01 <c>code</c> and <c>remedy</c> for one failed execution.
    ///
    /// Independent PR review finding: every script failure used to be reported as
    /// <c>script-execution-failed</c> with a null remedy, so the codes an agent is told to match on --
    /// <c>script-api-denied</c> and <c>script-lifecycle-confirmation-required</c> (skill.md, PRD §14) --
    /// only ever appeared as a SUBSTRING of <c>message</c> and never in the field that names them. A
    /// refusal an agent can lift by resending with one extra argument is worthless if the agent has to
    /// pattern-match prose to notice it, and worse, the confirmation refusal had the most obvious remedy
    /// of any error in the connector and carried none.
    ///
    /// Only exceptions that CARRY a code get one; anything else keeps the generic
    /// <c>script-execution-failed</c>, which is the honest answer for an arbitrary exception thrown by
    /// script code. ScriptAwaitNotAllowedException is here for exactly the same reason as the denylist
    /// pair -- it reaches this call site by the same route, defines its own code, and had the same gap.
    /// </summary>
    private static (string Code, string[]? Remedy) FailureCodeAndRemedy(Exception? exception) => exception switch
    {
        ScriptApiDenylistViolationException denial when denial.Code == ScriptApiDenylistViolationException.ConfirmationRequiredCode =>
            (denial.Code, new[]
            {
                "Resend the identical execute_script call with confirm_lifecycle_actions: true if this " +
                "action is genuinely intended.",
                $"Otherwise remove the use of {denial.DeniedMember} from the script.",
            }),
        ScriptApiDenylistViolationException denial =>
            (denial.Code, new[]
            {
                $"Remove {denial.DeniedMember} from the script; no argument to execute_script permits it.",
                "Make document changes directly instead -- the connector already runs every script inside " +
                "its own Transaction, which is committed on success and rolled back if the script throws. " +
                "For a savepoint within the run, a native Autodesk.Revit.DB.SubTransaction is permitted -- " +
                "hold it in a using and Commit/RollBack it before the enclosing block ends.",
            }),
        ScriptAwaitNotAllowedException =>
            (ScriptAwaitNotAllowedException.Code, new[]
            {
                "Rewrite the script without async/await; call the synchronous form of whatever it awaited.",
            }),
        // Issue #84. A compile failure is not "an arbitrary exception thrown by script code" -- it is the
        // one failure where the connector knows something the agent doesn't, and said nothing. Found live
        // building validation-corpus case #1: a script wrote `doc.Export(...)`, got back
        // "CS0103: The name 'doc' does not exist in the current context", and had no way to learn that the
        // global is `Document`. The names were never secret -- get_skills lists all ten with prose -- but
        // an agent that didn't call get_skills first had no path from the error to the answer.
        CompilationErrorException compilation => ("script-compilation-failed", CompilationRemedy(compilation)),
        // Issue #132. Revit offers no pre-write hook, so a script that forgets to wrap a write is
        // inevitable rather than unlikely -- and Revit's own message ("Attempt to modify the model
        // outside of transaction") names no way out. This is the same class of gap issue #84 closed for
        // CS0103: the connector knows exactly what the agent needs to do and was saying nothing.
        Exception modification when IsModificationOutsideTransaction(modification) =>
            ("script-write-outside-transaction", new[]
            {
                "Wrap the write in Connector.WithTransaction(document, () => { ... }) -- the connector " +
                "opens the transaction and commits it when the block ends. It works on any open document, " +
                "including one created or settled by an earlier call, and the value-returning form " +
                "(var id = Connector.WithTransaction(doc, () => ...)) hands back what the block produced.",
                "If this is inside Connector.WithoutTransaction, that block is deliberately not " +
                "modifiable; nest Connector.WithTransaction inside it to write.",
            }),
        // #146 Phase 0 (H10's inverse). The mirror image of the case above: a Revit API that manages its
        // OWN transaction -- Document.LoadFamily, UIDocument.RequestViewChange, every EditScope -- refuses
        // because the target IS modifiable, and under always-open that is the CONNECTOR's transaction the
        // script never asked for. Revit's message names the symptom and no way out; the fix is one wrap.
        Exception modifiable when IsTargetMustNotBeModifiable(modifiable) =>
            ("script-target-must-not-be-modifiable", new[]
            {
                "Wrap this call in Connector.WithoutTransaction(document, () => { ... }) -- the connector " +
                "closes its transaction for the block and restores it afterwards, so your other changes " +
                "still roll back if the script throws. To write inside that block, nest " +
                "Connector.WithTransaction.",
                "Document.LoadFamily needs BOTH documents non-modifiable: nest one WithoutTransaction " +
                "per document (source and target).",
            }),
        // #146 Phase 1 (H8). A native SubTransaction is permitted, and the one state it cannot start in
        // is "no open transaction" -- inside Connector.WithoutTransaction, or on a document this run never
        // wrote to. Revit's message is accurate and names no way to get a transaction open here.
        Exception subTransaction when IsSubTransactionOutsideTransaction(subTransaction) =>
            ("script-subtransaction-needs-transaction", new[]
            {
                "A SubTransaction is a savepoint INSIDE a transaction. Open one first: wrap this code in " +
                "Connector.WithTransaction(document, () => { ... }) and start the SubTransaction inside it.",
                "If this is inside Connector.WithoutTransaction, that block is deliberately not " +
                "modifiable; nest Connector.WithTransaction inside it.",
            }),
        _ => ("script-execution-failed", null),
    };

    /// <summary>
    /// Revit's wording for SubTransaction.Start() with no enclosing transaction, verified live (Revit
    /// 2025): "A sub-transaction can only be active inside an open Transaction." Message-matched for the
    /// same reason as <see cref="IsTargetMustNotBeModifiable"/>; fails open if reworded, pinned live.
    /// </summary>
    private static bool IsSubTransactionOutsideTransaction(Exception exception) =>
        exception.Message.Contains("sub-transaction can only be active inside an open Transaction", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Matched on the MESSAGE, and that is a weaker match than <see cref="IsModificationOutsideTransaction"/>'s
    /// by necessity: Revit reports these as <c>Autodesk.Revit.Exceptions.InvalidOperationException</c>, a
    /// type it also uses for hundreds of unrelated refusals, so the type alone says nothing. The phrases are
    /// Revit's own wording for the three known shapes -- the modifiability precondition ("must not be
    /// modifiable"), the active-view variant ("of a modifiable document"), and the EditScope commit edge,
    /// which is the same collision at the other end of the block (caveats.md, issue #115). Fails OPEN if
    /// Revit rewords them (the run simply reports script-execution-failed). The first two are pinned
    /// live against real Revit (TestTargetMustNotBeModifiableIsMappedToItsOwnCode); the EditScope phrase
    /// comes verbatim from the live trace recorded in caveats.md and is exercised only at tier 1.
    ///
    /// Reads the OUTER message only, on purpose: a refusal rewrapped by the script or surfaced through
    /// reflection (TargetInvocationException) falls to script-execution-failed rather than being matched
    /// by a recursive walk that would also match any inner exception a script chose to wrap.
    /// </summary>
    private static bool IsTargetMustNotBeModifiable(Exception exception)
    {
        var message = exception.Message;
        return message.Contains("must not be modifiable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("of a modifiable document", StringComparison.OrdinalIgnoreCase)
            || message.Contains("EditScope cannot be closed, for there is a transaction", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Matched on the type's FULL NAME rather than by a type pattern, and that is load-bearing rather
    /// than stylistic: a pattern naming Autodesk.Revit.Exceptions.ModificationOutsideTransactionException
    /// forces that type to resolve when THIS method is JITed, and MCPBridge.Core.Tests runs where
    /// RevitAPI.dll cannot load -- so every tier-1 test touching this call site would fail to load the
    /// assembly, silently, at `dotnet test` exit 0. Same hazard IConnectorRuntime documents for its own
    /// signatures, one rung over. The name is asserted live by the harness rather than trusted here,
    /// because a string typo would fail open: the mapping simply would not fire.
    /// </summary>
    private static bool IsModificationOutsideTransaction(Exception exception) =>
        exception.GetType().FullName == "Autodesk.Revit.Exceptions.ModificationOutsideTransactionException";

    /// <summary>
    /// Remedy lines for a compile failure. The globals are listed ONLY for CS0103 ("the name X does not
    /// exist in the current context"), because that is the diagnostic that means "you guessed an
    /// identifier" -- attaching ten names to every syntax error would be noise on the majority of compile
    /// failures, which are ordinary mistakes in the script's own code.
    ///
    /// <para>CS0246 ("type or namespace not found") is deliberately NOT included. It looks adjacent but
    /// means a missing using/reference, not a missing global, and pointing at the globals list there would
    /// send an agent looking in the wrong place.</para>
    /// </summary>
    private static string[]? CompilationRemedy(CompilationErrorException exception)
    {
        var unknownNameDiagnostics = exception.Diagnostics.Where(d => d.Id == "CS0103").ToArray();
        if (unknownNameDiagnostics.Length == 0)
        {
            return null;
        }

        // Taken from the diagnostic's MESSAGE ("The name 'doc' does not exist in the current context"),
        // not from its source span. The span route looked cleaner and did not work: these diagnostics come
        // back off a Roslyn scripting Compilation whose SourceTree is not reliably attached to the text the
        // span indexes into, so it silently produced an empty name and suppressed the whole remedy. The
        // message is a stable, documented part of the diagnostic; extracting from it is best-effort by
        // design, and the globals list -- the part that actually helps -- is emitted either way.
        var unknownNames = unknownNameDiagnostics
            .Select(d => Regex.Match(d.GetMessage(CultureInfo.InvariantCulture), "'([^']+)'"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var opening = unknownNames.Length == 0
            ? "A name in this script did not resolve."
            : $"{(unknownNames.Length == 1 ? "The name" : "The names")} " +
              string.Join(", ", unknownNames.Select(n => "'" + n + "'")) + " did not resolve.";

        return new[]
        {
            opening + " A script's scope carries exactly these globals: " +
            string.Join(", ", ScriptGlobals.GlobalNames) + ".",
            "Names are case-sensitive -- the document global is 'Document', not 'doc'.",
            // Both the namespace and the entry-point name are read from the type rather than spelled out,
            // so a rename cannot leave this line telling an agent to look somewhere that no longer exists.
            $"The connector's own functions are reached through '{nameof(ScriptGlobals.Connector)}' " +
            $"(e.g. {nameof(ScriptGlobals.Connector)}.Publish(path)); search_functions and " +
            $"describe_function index them under the {typeof(Connector).Namespace} namespace, alongside " +
            "Revit's own API.",
            "Call get_skills for the transaction, document-creation and file-exchange rules.",
        };
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
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, ex.Diagnostic);
        }

        // Real wall-clock elapsed for the wire-budget cap on the window inventory (#136), distinct from the
        // injected-clock deadline the poll loop waits against.
        var handlerSw = System.Diagnostics.Stopwatch.StartNew();
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
                // #136: same wire-budget cap as the execute_script timeout branch -- poll_execution carries
                // the identical timeout_ms + 5s broker budget, so an unbounded inventory here strands the
                // poll wire call the same way it stranded the start call.
                var extraNotices = await BuildWindowInventoryNoticesWithinWireBudget(handlerSw, timeoutMs).ConfigureAwait(false);
                return ExecutionResultMessage.FromRecord(request.Id, record, extraNotices);
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
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, ex.Diagnostic);
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
            : JsonRpcErrorMessage.ToJson(
                request.Id,
                JsonRpcErrorCode.InternalError,
                $"execution_id '{executionId}' vanished mid-cancellation.",
                DiagnosticRecord.Create(
                    DiagnosticSeverity.Error,
                    "execution-record-vanished",
                    DiagnosticSource.Execution,
                    $"execution_id '{executionId}' vanished mid-cancellation.",
                    detail: new Dictionary<string, object?> { ["execution_id"] = executionId },
                    // Its own code rather than folding into unknown-execution-id: this is the ring buffer
                    // evicting a record between RequestCancellation and the Poll a few lines above, which
                    // is a connector-side race, not the agent addressing something that never existed.
                    // Retrying is genuinely the right move here and genuinely the wrong move there.
                    remedy: new[] { "Re-issue cancel_execution, or poll_execution to read the execution's final state." }));
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
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, ex.Diagnostic);
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
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, ex.Diagnostic);
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
            // member is optional when member_id is supplied (issue #64) -- member_id alone is a reliable
            // disambiguator (see DiscoveryService.DescribeFunction's own doc comment for the full contract),
            // so at least one of the two, not necessarily member, is required.
            //
            // That "at least one" rule is enforced by DescribeFunction itself, NOT duplicated here. There
            // used to be a copy of the check at this line, and review of the issue-#69 change caught what
            // duplication had cost: the copy shadowed the real guard (so DiscoveryService's own branch was
            // unreachable through dispatch, and silently untested -- a mutation that broke its record
            // entirely still passed 1,252 tests), and the two copies had already drifted to two different
            // messages for the identical condition. Two shapes for one failure is the very thing issue #69
            // is about; keeping one guard is the fix, not keeping two in sync.
            var member = request.GetOptionalString("member");
            var memberId = request.GetOptionalString("member_id");

            var result = _discoveryService.DescribeFunction(member, memberId);
            return DiscoveryResultMessage.DescribeFunction(request.Id, result);
        }
        catch (JsonRpcParamException ex)
        {
            return JsonRpcErrorMessage.ToJson(request.Id, JsonRpcErrorCode.InvalidParams, ex.Message, ex.Diagnostic);
        }
        catch (DiscoveryMemberNotFoundException ex)
        {
            var diagnostic = DiagnosticRecord.Create(
                DiagnosticSeverity.Error,
                "discovery-member-not-found",
                DiagnosticSource.Discovery,
                ex.Message,
                detail: new Dictionary<string, object?>
                {
                    ["member"] = request.GetOptionalString("member"),
                    ["member_id"] = request.GetOptionalString("member_id"),
                },
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
        "unknown-execution-id",
        DiagnosticSource.Execution,
        $"execution_id '{executionId}' is not known to this add-in instance (never started, or evicted from the ring buffer).",
        detail: new Dictionary<string, object?> { ["execution_id"] = executionId },
        remedy: new[] { "Start a new execution with execute_script." });
}
