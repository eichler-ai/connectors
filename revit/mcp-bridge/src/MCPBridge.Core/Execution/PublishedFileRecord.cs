using System.Text.Json.Serialization;

namespace MCPBridge.Core.Execution;

/// <summary>
/// One entry in an execute_script/poll_execution result's `files[]` array (PRD §09: "Every
/// execute_script/poll_execution result also carries a files[] array alongside notices[] -- one
/// entry per file the script published as an output, each with its own per-file status"). Never
/// folded into notices[] -- it's a sibling list, matching PRD §01's diagnostic-record shape being
/// reserved for notices/errors/logs, not this.
/// </summary>
public sealed record PublishedFileRecord(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string? Message)
{
    public const string StatusPublished = "published";
    public const string StatusFailed = "failed";
}
