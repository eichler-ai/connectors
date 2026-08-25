using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace MCPBridge.Core.Diagnostics;

/// <summary>
/// The one shared diagnostic-record shape (PRD §01), reused for the `notices[]` array on
/// a successful result, the `data` field of a JSON-RPC error, and every NDJSON log line.
/// Immutable and constructed only via <see cref="Create"/> so the "message is a hard
/// rule, not a suggestion" requirement (non-empty, specific) is enforced at one point.
/// </summary>
public sealed class DiagnosticRecord
{
    [JsonPropertyName("severity")]
    public DiagnosticSeverity Severity { get; }

    [JsonPropertyName("code")]
    public string Code { get; }

    [JsonPropertyName("source")]
    public string Source { get; }

    [JsonPropertyName("message")]
    public string Message { get; }

    [JsonPropertyName("detail")]
    public IReadOnlyDictionary<string, object?> Detail { get; }

    [JsonPropertyName("remedy")]
    public IReadOnlyList<string> Remedy { get; }

    private DiagnosticRecord(
        DiagnosticSeverity severity,
        string code,
        string source,
        string message,
        IReadOnlyDictionary<string, object?> detail,
        IReadOnlyList<string> remedy)
    {
        Severity = severity;
        Code = code;
        Source = source;
        Message = message;
        Detail = detail;
        Remedy = remedy;
    }

    public static DiagnosticRecord Create(
        DiagnosticSeverity severity,
        string code,
        DiagnosticSource source,
        string message,
        IReadOnlyDictionary<string, object?>? detail,
        IReadOnlyList<string>? remedy)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("code must be a non-empty, stable machine-readable identifier.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException(
                "message must be specific and concrete (PRD §01) -- it cannot be empty.", nameof(message));
        }

        return new DiagnosticRecord(
            severity,
            code,
            source.ToSourceTag(),
            message,
            detail is null ? EmptyDetail : detail.ToDictionary(kv => kv.Key, kv => kv.Value),
            remedy is null ? Array.Empty<string>() : remedy.ToArray());
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyDetail =
        new Dictionary<string, object?>();

    public override string ToString() => $"[{Severity}] {Code} ({Source}): {Message}";
}
