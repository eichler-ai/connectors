using System.Text.Json;
using System.Text.Json.Serialization;
using Rhino.MCPBridge.Core.Execution;

namespace Rhino.MCPBridge.Core.Protocol;

/// <summary>restart_snapshot's wire result (PRD §10/§15): every open document's save state, so the server
/// can guard against discarding unsaved work and knows which saved files to reopen after the restart.
/// Read-only — the bridge does not exit; the server drives the restart. Keep in step with the Go side.</summary>
internal static class RestartSnapshotMessage
{
    private sealed class DocDto
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("path")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Path { get; set; }
        [JsonPropertyName("modified")] public bool Modified { get; set; }
    }

    private sealed class ResultDto
    {
        [JsonPropertyName("documents")] public List<DocDto> Documents { get; set; } = new();
    }

    private sealed class Envelope
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonPropertyName("id")] public JsonElement Id { get; set; }
        [JsonPropertyName("result")] public ResultDto Result { get; set; } = new();
    }

    public static string ToJson(JsonElement id, IReadOnlyList<DocumentSaveState> states)
    {
        var dto = new ResultDto();
        foreach (var s in states)
        {
            dto.Documents.Add(new DocDto { Kind = s.Kind, Title = s.Title, Path = s.Path, Modified = s.Modified });
        }

        return JsonSerializer.Serialize(new Envelope { Id = id, Result = dto }, WireJson.Compact);
    }
}
