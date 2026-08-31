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

    /// <summary>
    /// Serializes check-then-rotate-then-append, which is otherwise a read-modify-write race with real
    /// callers on both sides: the reconnect loop logs from its own worker thread, SyncDiscoveryCache
    /// from a Timer thread, and RequestDispatcher's auditTrailTrace from whichever thread is dispatching.
    ///
    /// <para>The dangerous interleaving is the one that SUCCEEDS, not the one that fails: two threads
    /// both see an at-cap file, the first rotates and appends a line, then the second rotates that
    /// one-line file over the 5MB generation the first just saved. Both generations of history are gone
    /// and .old holds a single line -- strictly worse than the log briefly running over its cap. A
    /// failed rename is the benign half of this race and is handled separately, in TryRotate.</para>
    ///
    /// <para>Cross-PROCESS the same interleaving is still reachable: two Revit instances share one local
    /// app-data directory and this lock is per-process. Left as-is rather than escalated to a named
    /// mutex, because the cost there is bounded (a truncated .old on a debug log) and a cross-process
    /// mutex is real machinery -- ownership, abandonment, a handle held for the life of the process --
    /// to protect a best-effort file. Recorded here rather than implied to be handled.</para>
    /// </summary>
    private static readonly object RotationLock = new();

    /// <param name="directory">
    /// Resolved INSIDE the guard below, not by the caller, so that a throwing path computation is
    /// swallowed like every other failure here. Both call sites compute it from
    /// BrokerDiscoveryOptions.Local(), whose contract is that a logging failure never masks the
    /// exception the caller was already reporting.
    /// </param>
    internal static void Append(Func<string> directory, string fileName, string message)
        => Append(directory, fileName, message, MaxBytes);

    internal static void Append(Func<string> directory, string fileName, string message, long maxBytes)
    {
        try
        {
            var resolved = directory();
            Directory.CreateDirectory(resolved);
            var path = Path.Combine(resolved, fileName);

            lock (RotationLock)
            {
                TryRotate(path, maxBytes);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}\n");
            }
        }
        catch
        {
            // Best-effort diagnostic only -- a failure here must never mask or interfere with whatever
            // the caller was already reporting, which handles its own failures independently.
        }
    }

    /// <summary>
    /// Rotation is guarded SEPARATELY from the append above, and that separation is the point: if
    /// rotation fails, the diagnostic line must still be written. Another process can hold the file or
    /// win the rename first, so a failed rotation is an ordinary outcome, not an exceptional one -- and
    /// silently dropping the very trace this file exists to preserve would be a strictly worse failure
    /// than the log briefly exceeding its cap.
    ///
    /// <para>The size check is pre-append, so a single message larger than the cap lands whole and is
    /// rotated out by the NEXT call. On-disk size is therefore bounded by cap + the largest single
    /// message, not by cap alone -- fine here, where the longest message is a stack trace.</para>
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
