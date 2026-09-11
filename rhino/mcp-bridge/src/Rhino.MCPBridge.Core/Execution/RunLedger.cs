using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// The connector's last completed run on a document (PRD §05 "observability only"): what
/// <c>list_instances</c> shows per document as <c>last_run</c>, what an execution result carries as
/// the run that preceded it, and what the undo tool names when it reverts the connector's own entry.
/// </summary>
public sealed class LastRun
{
    [JsonPropertyName("execution_id")] public string ExecutionId { get; init; } = "";
    /// <summary>The server process that issued the run, as it named itself at auth; the client an agent can recognise as its own.</summary>
    [JsonPropertyName("agent_client_id")] public string AgentClientId { get; init; } = "";
    [JsonPropertyName("finished_at")] public string FinishedAt { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("label")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Label { get; init; }
    /// <summary>Whether the run left an undo entry (it changed the document and completed).</summary>
    [JsonPropertyName("changed_document")] public bool ChangedDocument { get; init; }
    /// <summary>The ChangeClock tick at completion; never on the wire.</summary>
    [JsonIgnore] public long Tick { get; init; }
}

/// <summary>Per-document ledger of the last completed run; the plug-in owns one per instance.</summary>
internal sealed class RunLedger
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LastRun> _byDocument = new();
    private readonly Dictionary<string, LastRun> _lastChangingByDocument = new();

    /// <summary>The last completed run on the document, whatever it did (what list_instances shows).</summary>
    public LastRun? Get(string documentId)
    {
        lock (_lock)
        {
            return _byDocument.TryGetValue(documentId, out var r) ? r : null;
        }
    }

    /// <summary>The last completed run that CHANGED the document -- the one whose undo entry the undo
    /// tool reasons about; a read-only run since does not replace it.</summary>
    public LastRun? LastChanging(string documentId)
    {
        lock (_lock)
        {
            return _lastChangingByDocument.TryGetValue(documentId, out var r) ? r : null;
        }
    }

    /// <summary>Records <paramref name="run"/> as the document's latest and returns the one it replaced.</summary>
    public LastRun? Record(string documentId, LastRun run)
    {
        lock (_lock)
        {
            _byDocument.TryGetValue(documentId, out var previous);
            _byDocument[documentId] = run;
            if (run.ChangedDocument)
            {
                _lastChangingByDocument[documentId] = run;
            }

            return previous;
        }
    }

    /// <summary>Records an undo/redo as the document's latest run WITHOUT touching the last changing run.</summary>
    public void RecordTool(string documentId, LastRun op)
    {
        lock (_lock)
        {
            _byDocument[documentId] = op;
        }
    }

    public void Forget(string documentId)
    {
        lock (_lock)
        {
            _byDocument.Remove(documentId);
            _lastChangingByDocument.Remove(documentId);
        }
    }
}
