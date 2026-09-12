using Rhino.MCPBridge.Core.Diagnostics;

namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// One run, the Rhino way (rhino/docs/PRD.md §06/§07, spikes §3): the script executes inside a
/// connector-owned command, which makes the run one undo entry; on any failure the executor issues
/// Rhino's _Undo after the command has returned, which reverts exactly that entry. Mutations are
/// netted from document events for the run's duration. Everything that touched Rhino is behind
/// <see cref="IRunHost"/>, so this class is tier-1 tested against a fake.
///
/// Called on the main thread by the launcher; returns the completed outcome. Never throws for a
/// script's failure -- that is an outcome -- only for a host that is itself broken.
/// </summary>
internal sealed class UndoRunExecutor
{
    /// <summary>The name of the bridge's run command; the host's MostRecentCommandName must match it before an undo.</summary>
    public const string RunCommandName = "MCPBridgeRun";

    private readonly ScriptRunners _runners;
    private readonly IRunHost _host;
    private readonly ChangeClock _clock;

    public UndoRunExecutor(ScriptRunners runners, IRunHost host, ChangeClock? clock = null)
    {
        _runners = runners;
        _host = host;
        _clock = clock ?? new ChangeClock();
    }

    /// <summary>The clock the undo tool's gate reads; the adapter feeds it every document change.</summary>
    internal ChangeClock Clock => _clock;

    internal ScriptRunners Runners => _runners;
    internal IRunHost Host => _host;

    public sealed class Request
    {
        public required string ExecutionId { get; init; }
        public required string ScriptText { get; init; }
        /// <summary>"csharp" or "python"; the dispatcher has already checked the runner exists.</summary>
        public required string Language { get; init; }
        public required string DocumentId { get; init; }
        /// <summary>The Grasshopper definition to bind as the script's ghdoc/GrasshopperDocument (PRD §10);
        /// "" when the caller passed none, in which case the global is null.</summary>
        public string GrasshopperDocumentId { get; init; } = "";
        public required CancellationToken CancellationToken { get; init; }
        public bool ConfirmLifecycleActions { get; init; }
        public string? Label { get; init; }
        /// <summary>The server that issued the run, for the ledger (PRD §05); "" when it did not say.</summary>
        public string AgentClientId { get; init; } = "";
    }

    /// <summary>Returns the outcome, or null when the command could not start (another command holds
    /// Rhino) so the launcher can retry while the run stays `pending`.</summary>
    public ScriptExecutionOutcome? Execute(Request request)
    {
        var document = _host.ResolveDocument(request.DocumentId);
        ScriptExecutionOutcome? outcome;
        if (document is null)
        {
            outcome = ExecuteResolved(request, document);
        }
        else
        {
            using (_clock.EnterConnectorWork(document.DocumentId))
            {
                outcome = ExecuteResolved(request, document);
            }

            if (outcome is not null)
            {
                outcome.Tick = _clock.Next();
            }
        }

        if (outcome is not null && document is not null)
        {
            outcome.DocumentId = document.DocumentId;
        }

        return outcome;
    }

    private ScriptExecutionOutcome? ExecuteResolved(Request request, RunDocument? document)
    {
        if (document is null)
        {
            return ScriptExecutionOutcome.Failed(new DocumentNotFoundException(DocumentNotFound(request)), "");
        }

        // Resolve the addressed Grasshopper definition, if any, BEFORE entering the run command: a
        // gh_document_id that matches no open definition fails loudly rather than silently binding null.
        var grasshopperDocument = _host.ResolveGrasshopperDocument(request.GrasshopperDocumentId, out var grasshopperNotFound);
        if (grasshopperNotFound)
        {
            return ScriptExecutionOutcome.Failed(new GrasshopperDocumentNotFoundException(GrasshopperDocumentNotFound(request)), "");
        }

        var undoLabel = UndoLabel.For(request.Label);
        var mutations = new MutationTracker();
        var changed = false;
        GrasshopperReport? grasshopperReport = null;
        ScriptExecutionOutcome? outcome = null;
        var started = _host.RunInCommand(document, undoLabel, () =>
        {
            // Nothing may escape this body: it runs inside Rhino's native command dispatcher, and an
            // exception there is a crash class, not a failed run (review of #282). Everything from the
            // subscription to the runner is inside the try; a failure becomes the run's outcome.
            IDisposable? subscription = null;
            IGrasshopperSolveScope? solves = null;
            try
            {
                subscription = _host.SubscribeChanges(document, mutations.Record, () => changed = true);
                solves = _host.BeginGrasshopperSolves(grasshopperDocument);
                var globals = new ScriptGlobals((RhinoDoc)document.Raw!, request.CancellationToken, _host.BridgeVersion, request.Label, grasshopperDocument);
                var runner = _runners.Get(request.Language) ?? throw new InvalidOperationException($"no runner for language '{request.Language}'");
                outcome = runner.RunAsync(request.ScriptText, globals, request.CancellationToken, request.ConfirmLifecycleActions).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                outcome = ScriptExecutionOutcome.Failed(ex, "");
            }
            finally
            {
                // Build the solve report while still inside the command (all solves have ended), then release.
                try { grasshopperReport = solves?.BuildReport(); } catch { }
                try { solves?.Dispose(); } catch { }
                try { subscription?.Dispose(); } catch { }
            }
        });
        if (!started)
        {
            return null;
        }

        if (outcome is null)
        {
            // The command ran but the body never assigned -- a host bug, reported rather than hidden.
            return ScriptExecutionOutcome.Failed(new InvalidOperationException("the run command returned without producing an outcome"), "");
        }

        var report = mutations.Build();
        if (outcome.Success)
        {
            return new ScriptExecutionOutcome
            {
                Success = true, ReturnValue = outcome.ReturnValue, StdOut = outcome.StdOut, Notices = outcome.Notices, Files = outcome.Files,
                Mutations = report.IsEmpty ? null : report, ChangedDocument = changed || !report.IsEmpty, Grasshopper = grasshopperReport,
            };
        }

        // Failure or cancellation: revert the run's entry, then report what happened to it (PRD §07,
        // observability over silence). Nothing to revert only when NO document event fired -- the
        // report counts objects, but a layer, attribute or material change is a change too.
        var notices = new List<DiagnosticRecord>(outcome.Notices);
        var entryRemains = false;
        if (changed || !report.IsEmpty)
        {
            var rollback = Rollback(document, request.ExecutionId, report);
            notices.Add(rollback);
            // A skipped rollback leaves the run's entry on the stack: the ledger must know the run changed
            // the document, so the undo tool the notice points at can act on it.
            entryRemains = rollback.Code == "script-rollback-skipped";
        }

        var failed = outcome.WasCancelled
            ? ScriptExecutionOutcome.Cancelled(outcome.StdOut, notices, outcome.Files)
            : ScriptExecutionOutcome.Failed(outcome.Exception!, outcome.StdOut, notices, outcome.Files);
        failed.ChangedDocument = entryRemains;
        failed.Grasshopper = grasshopperReport; // diagnostics of what solved, reported even on failure
        return failed;
    }

    private DiagnosticRecord Rollback(RunDocument document, string executionId, MutationReport report)
    {
        var detail = new Dictionary<string, object?>
        {
            ["execution_id"] = executionId,
            ["net_added"] = report.NetAdded,
            ["net_modified"] = report.NetModified,
            ["net_deleted"] = report.NetDeleted,
        };
        if (!_host.LastCommandWasOurs())
        {
            return DiagnosticRecord.Create(DiagnosticSeverity.Warning, "script-rollback-skipped", DiagnosticSource.Execution,
                $"execution {executionId} failed after changing the document, but the most recent command Rhino ran is not the connector's, so its changes were NOT reverted -- reverting would undo a person's action.",
                detail, new[] { "Inspect the document; use the undo tool (confirm: true) once you have checked that the top undo entry is this run's." });
        }

        if (!_host.UndoLast(document))
        {
            return DiagnosticRecord.Create(DiagnosticSeverity.Warning, "script-rollback-skipped", DiagnosticSource.Execution,
                $"execution {executionId} failed after changing the document, but Rhino reported nothing to undo; its changes may remain.",
                detail, new[] { "Inspect the document and undo manually if needed." });
        }

        return DiagnosticRecord.Create(DiagnosticSeverity.Info, "script-rolled-back", DiagnosticSource.Execution,
            $"execution {executionId} failed after changing the document; the run's undo entry was reverted ({report.NetAdded} added, {report.NetModified} modified, {report.NetDeleted} deleted objects undone, plus any layer/attribute/table changes). Changes outside the document -- files written, commands with external effects -- are not undone by this.",
            detail, null);
    }

    private DiagnosticRecord DocumentNotFound(Request request)
    {
        var open = _host.OpenDocuments();
        return DiagnosticRecord.Create(DiagnosticSeverity.Error, "document-not-found", DiagnosticSource.Execution,
            request.DocumentId.Length == 0
                ? $"execution {request.ExecutionId} could not run: this Rhino instance has no active document."
                : $"execution {request.ExecutionId} could not run: no open document in this instance has document_id '{request.DocumentId}'.",
            new Dictionary<string, object?>
            {
                ["execution_id"] = request.ExecutionId,
                ["requested_document_id"] = request.DocumentId,
                ["open_documents"] = open.Select(d => new Dictionary<string, object?> { ["document_id"] = d.DocumentId, ["title"] = d.Title, ["active"] = d.Active }).ToList(),
            },
            new[] { "Pick a document_id from open_documents in this error's detail (or call list_instances), then retry." });
    }

    private DiagnosticRecord GrasshopperDocumentNotFound(Request request)
    {
        return DiagnosticRecord.Create(DiagnosticSeverity.Error, "grasshopper-document-not-found", DiagnosticSource.Execution,
            $"execution {request.ExecutionId} could not run: no open Grasshopper definition in this instance has gh_document_id '{request.GrasshopperDocumentId}'.",
            new Dictionary<string, object?>
            {
                ["execution_id"] = request.ExecutionId,
                ["requested_gh_document_id"] = request.GrasshopperDocumentId,
            },
            new[] { "Pick a gh_document_id from grasshopper_documents in list_instances (Grasshopper must be open and the definition loaded), then retry; or omit it to run without a bound definition." });
    }
}

/// <summary>Carries a ready-made §01 record for the document-not-found refusal.</summary>
public sealed class DocumentNotFoundException : Exception
{
    public DiagnosticRecord Record { get; }
    public DocumentNotFoundException(DiagnosticRecord record) : base(record.Message) { Record = record; }
}

/// <summary>Carries a ready-made §01 record for the grasshopper-document-not-found refusal (PRD §10).</summary>
public sealed class GrasshopperDocumentNotFoundException : Exception
{
    public DiagnosticRecord Record { get; }
    public GrasshopperDocumentNotFoundException(DiagnosticRecord record) : base(record.Message) { Record = record; }
}
