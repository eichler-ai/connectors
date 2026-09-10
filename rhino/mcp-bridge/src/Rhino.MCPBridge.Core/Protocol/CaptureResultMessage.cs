using System.Text.Json;
using System.Text.Json.Serialization;
using Rhino.MCPBridge.Core.Capture;
using Rhino.MCPBridge.Core.Diagnostics;

namespace Rhino.MCPBridge.Core.Protocol;

/// <summary>capture_view's wire result: the images base64-encoded (the server turns them into MCP
/// image content) plus notices. Keep in step with the Go side's capture.Result.</summary>
internal static class CaptureResultMessage
{
    private sealed class ImageDto
    {
        [JsonPropertyName("viewport")] public string Viewport { get; set; } = "";
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("mime_type")] public string MimeType { get; set; } = "image/png";
        [JsonPropertyName("data_base64")] public string DataBase64 { get; set; } = "";
    }

    private sealed class ResultDto
    {
        [JsonPropertyName("images")] public List<ImageDto> Images { get; set; } = new();
        [JsonPropertyName("notices")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<DiagnosticRecord>? Notices { get; set; }
    }

    private sealed class Envelope
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonPropertyName("id")] public JsonElement Id { get; set; }
        [JsonPropertyName("result")] public ResultDto Result { get; set; } = new();
    }

    public static string ToJson(JsonElement id, ViewCaptureService.Result result)
    {
        var dto = new ResultDto();
        foreach (var img in result.Images)
        {
            dto.Images.Add(new ImageDto { Viewport = img.Viewport, Width = img.Width, Height = img.Height, DataBase64 = Convert.ToBase64String(img.Png) });
        }

        if (result.Notices.Count > 0)
        {
            dto.Notices = result.Notices;
        }

        return JsonSerializer.Serialize(new Envelope { Id = id, Result = dto }, WireJson.Compact);
    }
}
