using System.Text.Json.Serialization;

namespace MCPBridge.Core.Diagnostics;

/// <summary>
/// Matches the severity values in the shared diagnostic-record shape (PRD §01). Wire
/// values are lowercase, matching the Go broker's diag.Severity constants (diag.go)
/// exactly -- a diagnostic record can be authored by either side and passed through
/// verbatim to the agent-facing response, so the two sides' spelling must agree.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticSeverity
{
    [JsonStringEnumMemberName("debug")]
    Debug,

    [JsonStringEnumMemberName("info")]
    Info,

    [JsonStringEnumMemberName("warning")]
    Warning,

    [JsonStringEnumMemberName("error")]
    Error,
}
