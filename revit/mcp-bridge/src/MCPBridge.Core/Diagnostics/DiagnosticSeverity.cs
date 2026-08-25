using System.Text.Json.Serialization;

namespace MCPBridge.Core.Diagnostics;

/// <summary>Matches the severity values in the shared diagnostic-record shape (PRD §01).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticSeverity
{
    Debug,
    Info,
    Warning,
    Error,
}
