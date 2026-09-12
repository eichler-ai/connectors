using System.Text.Json;
using System.Text.Json.Serialization;
using Rhino.MCPBridge.Core.Execution;

namespace Rhino.MCPBridge.Core.Protocol;

/// <summary>inspect_gh_definition's wire result (PRD §10): a read-only snapshot of an open Grasshopper
/// definition's objects, their canvas positions, and their component-level wiring, so an agent can see
/// what is on the canvas and frame a capture. Keep in step with the Go side (execution.InspectResult).</summary>
internal static class InspectDefinitionMessage
{
    private sealed class ObjectDto
    {
        [JsonPropertyName("guid")] public string Guid { get; set; } = "";
        [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("pivot")] public double[] Pivot { get; set; } = System.Array.Empty<double>();
        [JsonPropertyName("bounds")] public double[] Bounds { get; set; } = System.Array.Empty<double>();
        [JsonPropertyName("upstream")] public string[] Upstream { get; set; } = System.Array.Empty<string>();
        [JsonPropertyName("downstream")] public string[] Downstream { get; set; } = System.Array.Empty<string>();
    }

    private sealed class ResultDto
    {
        [JsonPropertyName("gh_document_id")] public string GrasshopperDocumentId { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("path")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Path { get; set; }
        [JsonPropertyName("object_count")] public int ObjectCount { get; set; }
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("match_count")] public int MatchCount { get; set; }
        [JsonPropertyName("offset")] public int Offset { get; set; }
        [JsonPropertyName("truncated")] public bool Truncated { get; set; }
        [JsonPropertyName("objects")] public List<ObjectDto> Objects { get; set; } = new();
    }

    private sealed class Envelope
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonPropertyName("id")] public JsonElement Id { get; set; }
        [JsonPropertyName("result")] public ResultDto Result { get; set; } = new();
    }

    public static string ToJson(JsonElement id, GrasshopperDefinitionInfo info)
    {
        var dto = new ResultDto
        {
            GrasshopperDocumentId = info.GrasshopperDocumentId,
            Title = info.Title,
            Path = info.Path,
            ObjectCount = info.ObjectCount,
            Enabled = info.Enabled,
            MatchCount = info.MatchCount,
            Offset = info.Offset,
            Truncated = info.Truncated,
        };
        foreach (var o in info.Objects)
        {
            dto.Objects.Add(new ObjectDto
            {
                Guid = o.Guid,
                Nickname = o.Nickname,
                Name = o.Name,
                Kind = o.Kind,
                Pivot = o.Pivot,
                Bounds = o.Bounds,
                Upstream = o.Upstream,
                Downstream = o.Downstream,
            });
        }

        return JsonSerializer.Serialize(new Envelope { Id = id, Result = dto }, WireJson.Compact);
    }
}
