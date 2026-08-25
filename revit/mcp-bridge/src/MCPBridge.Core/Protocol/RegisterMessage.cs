using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCPBridge.Core.Protocol;

/// <summary>
/// The `register` notification the add-in sends on every successful connect (first
/// connect and every reconnect alike, PRD §05), carrying the stable per-process
/// instance_id plus the auth token read from broker.json, presented on the TCP
/// handshake per PRD §10.
/// </summary>
public sealed class RegisterMessage
{
    private readonly Guid _instanceId;
    private readonly int _pid;
    private readonly string _revitVersion;
    private readonly IReadOnlyList<RegisteredDocument> _documents;
    private readonly string _authToken;

    public RegisterMessage(Guid instanceId, int pid, string revitVersion, IReadOnlyList<RegisteredDocument> documents, string authToken)
    {
        _instanceId = instanceId;
        _pid = pid;
        _revitVersion = revitVersion;
        _documents = documents;
        _authToken = authToken;
    }

    private sealed class DocumentDto
    {
        [JsonPropertyName("document_id")]
        public string DocumentId { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("workshared")]
        public bool Workshared { get; set; }

        [JsonPropertyName("active")]
        public bool Active { get; set; }
    }

    private sealed class ParamsDto
    {
        [JsonPropertyName("instance_id")]
        public string InstanceId { get; set; } = "";

        [JsonPropertyName("pid")]
        public int Pid { get; set; }

        [JsonPropertyName("revit_version")]
        public string RevitVersion { get; set; } = "";

        [JsonPropertyName("documents")]
        public List<DocumentDto> Documents { get; set; } = new();

        [JsonPropertyName("token")]
        public string Token { get; set; } = "";
    }

    private sealed class Envelope
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("method")]
        public string Method { get; set; } = "register";

        [JsonPropertyName("params")]
        public ParamsDto Params { get; set; } = new();
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Compact, single-line output -- NDJSON framing requires no embedded newlines.
        WriteIndented = false,
    };

    public string ToJson()
    {
        var envelope = new Envelope
        {
            Params = new ParamsDto
            {
                InstanceId = _instanceId.ToString(),
                Pid = _pid,
                RevitVersion = _revitVersion,
                Token = _authToken,
                Documents = ToDtoList(_documents),
            },
        };

        return JsonSerializer.Serialize(envelope, SerializerOptions);
    }

    private static List<DocumentDto> ToDtoList(IReadOnlyList<RegisteredDocument> documents)
    {
        var list = new List<DocumentDto>(documents.Count);
        foreach (var doc in documents)
        {
            list.Add(new DocumentDto
            {
                DocumentId = doc.DocumentId,
                Title = doc.Title,
                Path = doc.Path,
                Workshared = doc.IsWorkshared,
                Active = doc.IsActive,
            });
        }

        return list;
    }
}
