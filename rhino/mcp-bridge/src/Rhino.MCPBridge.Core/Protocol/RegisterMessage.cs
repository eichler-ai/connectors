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

    private sealed class GrasshopperDocumentDto
    {
        [JsonPropertyName("gh_document_id")] public string GrasshopperDocumentId { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("active")] public bool Active { get; set; }
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("component_count")] public int ComponentCount { get; set; }
    }

    private sealed class ParamsDto
    {
        [JsonPropertyName("instance_id")] public string InstanceId { get; set; } = "";
        [JsonPropertyName("pid")] public int Pid { get; set; }
        [JsonPropertyName("rhino_version")] public string RhinoVersion { get; set; } = "";
        [JsonPropertyName("platform")] public string Platform { get; set; } = "";
        [JsonPropertyName("bridge_version")] public string BridgeVersion { get; set; } = "";
        [JsonPropertyName("documents")] public List<DocumentDto> Documents { get; set; } = new();
        // Instance-level (PRD §10); omitted from the wire when empty so a Grasshopper-less session's
        // register looks exactly as it did before this field existed.
        [JsonPropertyName("gh_documents")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public List<GrasshopperDocumentDto>? GrasshopperDocuments { get; set; }
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

        List<GrasshopperDocumentDto>? ghDocs = null;
        if (snapshot.GrasshopperDocuments.Count > 0)
        {
            ghDocs = new List<GrasshopperDocumentDto>(snapshot.GrasshopperDocuments.Count);
            foreach (var g in snapshot.GrasshopperDocuments)
            {
                ghDocs.Add(new GrasshopperDocumentDto
                {
                    GrasshopperDocumentId = g.GrasshopperDocumentId,
                    Title = g.Title,
                    Path = g.Path,
                    Active = g.IsActive,
                    Enabled = g.IsEnabled,
                    ComponentCount = g.ComponentCount,
                });
            }
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
                GrasshopperDocuments = ghDocs,
                ExecutionState = snapshot.ExecutionState,
            },
        }, WireJson.Compact);
    }
}
