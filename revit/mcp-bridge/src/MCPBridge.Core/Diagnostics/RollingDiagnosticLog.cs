using System;
using System.IO;

namespace MCPBridge.Core.Diagnostics;

/// <summary>
/// Best-effort append to one of the add-in's local diagnostic logs, with size-based rotation.
///
/// <para>These logs exist because of PRD §01's observability rule: a caught-and-swallowed failure still
/// deserves a trace somewhere rather than total silence. That rule is what makes them unbounded by
/// default -- the reconnect loop retries forever and logs on every failed attempt (observed firing
/// roughly every 30s through an outage), so the file grows for as long as Revit runs and is never
/// cleaned up by anything (issue #11).</para>
///
/// <para>Rotation is deliberately the crudest scheme that bounds the file: when the log has reached the
/// cap, it is renamed over <c>&lt;name&gt;.old</c> and a fresh one started. That keeps at most two
/// generations -- roughly 2x the cap on disk -- and keeps the most recent history, which is the half a
/// human debugging a live failure actually reads. No date-stamped archive set, no compaction: those
/// would add a retention policy (and its own bugs) to a file whose entire job is to be readable when
/// something else has already gone wrong.</para>
///
/// <para>Internal because in this assembly <c>public</c> means script-reachable; the AddIn reaches it
/// through MCPBridge.Core.csproj's InternalsVisibleTo grant.</para>
/// </summary>
internal static class RollingDiagnosticLog
{
    /// <summary>
    /// Rotation threshold. Large enough that an ordinary session never rotates at all (so the common
    /// case is byte-for-byte what it was before rotation existed), small enough that two generations
    /// stay trivially openable in an editor on the machine being debugged.
    /// </summary>
    internal const long MaxBytes = 5L * 1024 * 1024;

    internal static void Append(string directory, string fileName, string message)
        => Append(directory, fileName, message, MaxBytes);

    internal static void Append(string directory, string fileName, string message, long maxBytes)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            TryRotate(path, maxBytes);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}\n");
        }
        catch
        {
            // Best-effort diagnostic only -- a failure here must never mask or interfere with whatever
            // the caller was already reporting, which handles its own failures independently.
        }
    }

    /// <summary>
    /// Rotation is guarded SEPARATELY from the append above, and that separation is the point: if
    /// rotation fails, the diagnostic line must still be written. Two Revit instances share one local
    /// app-data directory and can race here, so a failed rename is an ordinary outcome, not an
    /// exceptional one -- and silently dropping the very trace this file exists to preserve would be a
    /// strictly worse failure than the log briefly exceeding its cap.
    /// </summary>
    private static void TryRotate(string path, long maxBytes)
    {
        try
        {
            var existing = new FileInfo(path);
            if (!existing.Exists || existing.Length < maxBytes)
            {
                return;
            }

            // overwrite: the previous rotation's file is the one generation we deliberately discard.
            File.Move(path, path + ".old", overwrite: true);
        }
        catch
        {
            // See the summary above: keep going and append to the oversize file.
        }
    }
}
