using System;
using System.Collections.Generic;
using Rhino.MCPBridge.Core.Diagnostics;

namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// The <c>undo</c>/<c>redo</c> tools (PRD §07). Rhino gives the connector one piece of evidence Revit
/// never had: <c>Command.LastCommandId</c> says which command produced the top undo entry. So the
/// gate is by evidence, not by blanket confirmation -- an undo whose top entry is the bridge's own run
/// command, or a redo right after the connector's own undo, runs without <c>confirm</c>; anything
/// else (a person acted since) is refused with <c>undo-confirmation-required</c> naming that command,
/// and runs with <c>confirm: true</c> under a warning. Runs on the main thread after the busy gate,
/// as a script does; the reverted change is reported through the same mutation report.
/// </summary>
internal sealed class UndoRedoExecutor
{
    public enum Direction { Undo, Redo }

    public sealed class Outcome
    {
        public DiagnosticRecord? Error { get; init; }
        public IReadOnlyList<DiagnosticRecord> Notices { get; init; } = Array.Empty<DiagnosticRecord>();
        public MutationReport? Mutations { get; init; }
    }

    private readonly IRunHost _host;
    private readonly RunLedger _ledger;
    private readonly ChangeClock _clock;
    private readonly object _lock = new();

    // The connector's own last undo/redo: which, and when (a clock tick), so "nothing foreign
    // happened since" is a tick comparison.
    private Direction? _lastToolDirection;
    private long _lastToolTick;

    public UndoRedoExecutor(IRunHost host, RunLedger ledger, ChangeClock clock)
    {
        _host = host;
        _ledger = ledger;
        _clock = clock;
    }

    /// <summary>
    /// Whether the operation acts on the connector's own work, from the clock (PRD §07):
    /// - undo: the connector's last run on the document changed it and nothing foreign changed the
    ///   document since (nor did the connector undo it already); or the connector's own redo was the
    ///   last thing to touch the document.
    /// - redo: the connector's own undo was the last thing to touch the document, and no run of the
    ///   connector's changed it since (which would have emptied the redo stack).
    /// </summary>
    private bool IsOurs(Direction direction, string documentId, LastRun? lastRun)
    {
        var foreign = _clock.LastForeignChange(documentId);
        lock (_lock)
        {
            var runTick = lastRun is { ChangedDocument: true } ? lastRun.Tick : 0;
            return direction == Direction.Undo
                ? (runTick > foreign && runTick > _lastToolTick)
                    || (_lastToolDirection == Direction.Redo && _lastToolTick > foreign && _lastToolTick > runTick)
                : _lastToolDirection == Direction.Undo && _lastToolTick > foreign && _lastToolTick > runTick;
        }
    }

    /// <summary>Called on the main thread.</summary>
    public Outcome Execute(Direction direction, string documentId, bool confirm, string executionId, DateTimeOffset now)
    {
        var word = direction == Direction.Undo ? "undo" : "redo";
        var document = _host.ResolveDocument(documentId);
        if (document is null)
        {
            var open = _host.OpenDocuments();
            return new Outcome
            {
                Error = DiagnosticRecord.Create(DiagnosticSeverity.Error, "document-not-found", DiagnosticSource.Execution,
                    documentId.Length == 0 ? "this Rhino instance has no active document" : $"no open document has document_id '{documentId}'",
                    new Dictionary<string, object?> { ["requested_document_id"] = documentId, ["open_documents"] = open },
                    new[] { "call list_instances and pick a document_id" }),
            };
        }

        var (_, lastName) = _host.LastCommand();
        var lastRun = _ledger.LastChanging(document.DocumentId);
        var ours = IsOurs(direction, document.DocumentId, lastRun);
        if (!ours && !confirm)
        {
            return new Outcome
            {
                Error = DiagnosticRecord.Create(DiagnosticSeverity.Error, "undo-confirmation-required", DiagnosticSource.Execution,
                    direction == Direction.Undo
                        ? $"the top of the undo stack is not the connector's work (the document changed outside the connector's runs since its last change here; the last command Rhino ran was '{lastName}'), so {word} would revert a person's action. Nothing was done."
                        : $"the connector's own undo is not the last thing that touched this document (the last command Rhino ran was '{lastName}'), so {word} would restore something the connector did not revert. Nothing was done.",
                    new Dictionary<string, object?> { ["last_command"] = lastName, ["last_run"] = lastRun },
                    new[]
                    {
                        $"Resend with confirm: true if reverting '{lastName}' is genuinely intended.",
                        "To undo only a mistake inside a script, roll back there instead: raise/throw and the connector reverts the run.",
                    }),
            };
        }

        var mutations = new MutationTracker();
        var changed = false;
        bool done;
        using (_clock.EnterConnectorWork())
        using (_host.SubscribeChanges(document, mutations.Record, () => changed = true))
        {
            done = direction == Direction.Undo ? _host.UndoLast(document) : _host.RedoLast(document);
        }

        lock (_lock)
        {
            _lastToolDirection = direction;
            _lastToolTick = _clock.Next();
        }

        var report = mutations.Build();
        if (!done && !changed && report.IsEmpty)
        {
            return new Outcome
            {
                Error = DiagnosticRecord.Create(DiagnosticSeverity.Error, $"{word}-nothing-to-{word}", DiagnosticSource.Execution,
                    $"Rhino reported nothing to {word} on document '{document.DocumentId}' (or refused the command because another command is running).",
                    new Dictionary<string, object?> { ["document_id"] = document.DocumentId, ["last_command"] = lastName },
                    new[] { "Nothing was changed. Check the Undo menu in Rhino if this is unexpected." }),
            };
        }

        var detail = new Dictionary<string, object?>
        {
            ["direction"] = word,
            ["document_id"] = document.DocumentId,
            ["last_command"] = lastName,
            ["last_run"] = lastRun,
        };
        var notice = ours
            ? DiagnosticRecord.Create(DiagnosticSeverity.Info, "undo-reverted-connector-work", DiagnosticSource.Execution,
                $"{word} acted on the connector's own work" + (lastRun is null ? "." : $" (run {lastRun.ExecutionId}{(lastRun.Label is null ? "" : $", label '{lastRun.Label}'")}, finished {lastRun.FinishedAt})."),
                detail, null)
            : DiagnosticRecord.Create(DiagnosticSeverity.Warning, "undo-reverted-other-work", DiagnosticSource.Execution,
                $"{word} acted on '{lastName}', which was NOT the connector's work -- a person's action was reverted or restored, as confirmed.",
                detail, new[] { $"If that was not intended, call {(direction == Direction.Undo ? "redo" : "undo")} now, before anything else changes." });

        return new Outcome { Notices = new[] { notice }, Mutations = report.IsEmpty ? null : report };
    }
}
