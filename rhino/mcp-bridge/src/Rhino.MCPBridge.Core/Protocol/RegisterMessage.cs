using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rhino.MCPBridge.Core.Protocol;

/// <summary>
/// The `register` notification (PRD §05): sent by the plug-in right after a server's auth succeeds,
/// and again on every document event. Same message both times, replace semantics on the server.
/// </summary>
public static class RegisterMessage
{
    private sealed class DocumentDto
    {
        [JsonPropertyName("document_id")] public string DocumentId { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("active")] public bool Active { get; set; }
        [JsonPropertyName("last_run")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Execution.LastRun? LastRun { get; set; }
    }

    private sealed class ParamsDto
    {
        [JsonPropertyName("instance_id")] public string InstanceId { get; set; } = "";
        [JsonPropertyName("pid")] public int Pid { get; set; }
        [JsonPropertyName("rhino_version")] public string RhinoVersion { get; set; } = "";
        [JsonPropertyName("platform")] public string Platform { get; set; } = "";
        [JsonPropertyName("bridge_version")] public string BridgeVersion { get; set; } = "";
        [JsonPropertyName("documents")] public List<DocumentDto> Documents { get; set; } = new();
        [JsonPropertyName("execution_state")] public string ExecutionState { get; set; } = "idle";
    }

    private sealed class Envelope
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonPropertyName("method")] public string Method { get; set; } = "register";
        [JsonPropertyName("params")] public ParamsDto Params { get; set; } = new();
    }

    public static string ToJson(RegisterSnapshot snapshot)
    {
        var docs = new List<DocumentDto>(snapshot.Documents.Count);
        foreach (var d in snapshot.Documents)
        {
            docs.Add(new DocumentDto { DocumentId = d.DocumentId, Title = d.Title, Path = d.Path, Active = d.IsActive, LastRun = d.LastRun });
        }

        return JsonSerializer.Serialize(new Envelope
        {
            Params = new ParamsDto
            {
                InstanceId = snapshot.InstanceId.ToString(),
                Pid = snapshot.Pid,
                RhinoVersion = snapshot.RhinoVersion,
                Platform = snapshot.Platform,
                BridgeVersion = snapshot.BridgeVersion,
                Documents = docs,
                ExecutionState = snapshot.ExecutionState,
            },
        }, WireJson.Compact);
    }
}
