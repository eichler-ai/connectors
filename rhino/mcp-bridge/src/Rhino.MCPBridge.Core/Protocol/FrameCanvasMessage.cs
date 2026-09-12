using System.Text.Json;
using System.Text.Json.Serialization;
using Rhino.MCPBridge.Core.Execution;

namespace Rhino.MCPBridge.Core.Protocol;

/// <summary>frame_canvas's wire result (PRD §11): which canvas region the viewport was set to frame, and how
/// the neighbourhood resolved. Keep in step with the Go side (execution.FrameResult).</summary>
internal static class FrameCanvasMessage
{
    private sealed class ResultDto
    {
        [JsonPropertyName("gh_document_id")] public string GrasshopperDocumentId { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("rect")] public double[] Rect { get; set; } = System.Array.Empty<double>();
        [JsonPropertyName("framed_object_count")] public int FramedObjectCount { get; set; }
        [JsonPropertyName("matched_components")] public string[] MatchedComponents { get; set; } = System.Array.Empty<string>();
        [JsonPropertyName("missing_components")] public string[] MissingComponents { get; set; } = System.Array.Empty<string>();
        [JsonPropertyName("framed_whole_definition")] public bool FramedWholeDefinition { get; set; }
    }

    private sealed class Envelope
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonPropertyName("id")] public JsonElement Id { get; set; }
        [JsonPropertyName("result")] public ResultDto Result { get; set; } = new();
    }

    public static string ToJson(JsonElement id, FrameCanvasResult r)
    {
        var dto = new ResultDto
        {
            GrasshopperDocumentId = r.GrasshopperDocumentId,
            Title = r.Title,
            Rect = r.Rect,
            FramedObjectCount = r.FramedObjectCount,
            MatchedComponents = r.MatchedComponents,
            MissingComponents = r.MissingComponents,
            FramedWholeDefinition = r.FramedWholeDefinition,
        };
        return JsonSerializer.Serialize(new Envelope { Id = id, Result = dto }, WireJson.Compact);
    }
}
