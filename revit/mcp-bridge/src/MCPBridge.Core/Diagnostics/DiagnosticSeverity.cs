using System.Text.Json.Serialization;
using MCPBridge.Core.Protocol;

namespace MCPBridge.Core.Diagnostics;

/// <summary>
/// Matches the severity values in the shared diagnostic-record shape (PRD §01). Wire
/// values are lowercase, matching the Go broker's diag.Severity constants (diag.go)
/// exactly -- a diagnostic record can be authored by either side and passed through
/// verbatim to the agent-facing response, so the two sides' spelling must agree.
/// </summary>
[JsonConverter(typeof(WireEnumNameConverter<DiagnosticSeverity>))]
public enum DiagnosticSeverity
{
    [WireEnumName("debug")]
    Debug,

    [WireEnumName("info")]
    Info,

    [WireEnumName("warning")]
    Warning,

    [WireEnumName("error")]
    Error,
}
