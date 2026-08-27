using System.Collections.Generic;
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

/// <summary>
/// Bridges a running script's <see cref="ScriptGlobals.Publish"/> calls to
/// <see cref="TransactionScriptExecutor"/>, which has no other route to "which exports directory
/// and overwrite policy applies to whichever script happens to be running right now." Mirrors
/// <see cref="ActiveDialogContext"/> exactly in shape, for the same reason that class is a plain
/// static: ExecutionManager guarantees at most one active (non-terminal) execution per Revit
/// instance at a time, so there is never more than one script's export context live at once.
///
/// Two-way, like ActiveDialogContext: every file a script tries to publish -- whether the copy
/// succeeds or fails -- gets recorded here, drained once per script run by
/// TransactionScriptExecutor and folded into that execution's files[] (PRD §09's invariant:
/// files[] is never conditional on the run's own outcome -- Completed, Failed, and Cancelled
/// results alike must include whatever was published before the run stopped).
/// </summary>
public static class ActiveExportContext
{
    private static string? _exportsDirectoryPath;
    private static bool _overwriteOutputFiles;
    private static List<PublishedFileRecord> _recorded = new();

    /// <summary>True only while a script with an exports directory is actually running.</summary>
    public static bool IsActive => _exportsDirectoryPath is not null;

    /// <summary>The active script's exports directory, or null if no export context is active.</summary>
    public static string? ExportsDirectoryPath => _exportsDirectoryPath;

    /// <summary>The active script's overwrite_output_files flag (PRD §09), default false when inactive.</summary>
    public static bool OverwriteOutputFiles => _overwriteOutputFiles;

    public static void SetActive(string exportsDirectoryPath, bool overwriteOutputFiles)
    {
        _exportsDirectoryPath = exportsDirectoryPath;
        _overwriteOutputFiles = overwriteOutputFiles;
        _recorded = new List<PublishedFileRecord>();
    }

    public static void ClearActive()
    {
        _exportsDirectoryPath = null;
        _overwriteOutputFiles = false;
        _recorded = new List<PublishedFileRecord>();
    }

    public static void RecordPublished(PublishedFileRecord record) => _recorded.Add(record);

    public static IReadOnlyList<PublishedFileRecord> DrainRecorded()
    {
        var drained = _recorded;
        _recorded = new List<PublishedFileRecord>();
        return drained;
    }
}
