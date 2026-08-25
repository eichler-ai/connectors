using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCPBridge.Core.Protocol;

/// <summary>
/// The `register` notification the add-in sends immediately after a successful `auth`
/// handshake, on every successful connect (first connect and every reconnect alike,
/// PRD §05), carrying the stable per-process instance_id, pid, Revit version, and open
/// documents. The auth token does NOT ride on this message -- it is presented once, up
/// front, in the separate `auth` request that must be the very first message on the
/// connection (see <see cref="AuthMessage"/> and PRD §10); the Go broker's registerParams
/// struct has no token field at all, so embedding one here would be silently ignored.
/// </summary>
public sealed class RegisterMessage
{
    private readonly Guid _instanceId;
    private readonly int _pid;
    private readonly string _revitVersion;
    private readonly IReadOnlyList<RegisteredDocument> _documents;

    public RegisterMessage(Guid instanceId, int pid, string revitVersion, IReadOnlyList<RegisteredDocument> documents)
    {
        _instanceId = instanceId;
        _pid = pid;
        _revitVersion = revitVersion;
        _documents = documents;
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

    public string ToJson()
    {
        var envelope = new Envelope
        {
            Params = new ParamsDto
            {
                InstanceId = _instanceId.ToString(),
                Pid = _pid,
                RevitVersion = _revitVersion,
                Documents = ToDtoList(_documents),
            },
        };

        return JsonSerializer.Serialize(envelope, WireJson.Compact);
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
