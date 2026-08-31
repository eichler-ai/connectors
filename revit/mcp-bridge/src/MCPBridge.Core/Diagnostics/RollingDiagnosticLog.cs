using System;
using System.IO;
using System.Text;

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
/// <para>Free text, not <see cref="DiagnosticRecord"/>s, despite sharing this namespace and §01's
/// motivation: these files are read by a human debugging a machine, not parsed. The namespace is shared
/// because the concept is -- §01 is where the requirement to write them at all comes from.</para>
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
    ///
    /// <para>There is deliberately NO per-call cap parameter, and that is a correctness decision rather
    /// than a simplification. An earlier revision took one, defaulting to this constant. A test then
    /// pinned the default overload -- but every caller here is reached through InternalsVisibleTo, so
    /// `Append(..., long.MaxValue)` written at a CALL SITE still restored issue #11 in full with the
    /// whole suite green. A test cannot close that; removing the parameter can. Tests exercise this
    /// constant directly instead, seeding sparse files so a 5MB threshold costs nothing.</para>
    /// </summary>
    internal const long MaxBytes = 5L * 1024 * 1024;

    /// <summary>
    /// Serializes check-then-rotate-then-append, which is otherwise a read-modify-write race with real
    /// callers on both sides: the reconnect loop logs from its own worker thread, SyncDiscoveryCache
    /// from a Timer thread, ExecutionAuditTrail's retention sweep from a Task.Run, and
    /// RequestDispatcher's auditTrailTrace from whichever thread is dispatching.
    ///
    /// <para>The dangerous interleaving is the one that SUCCEEDS, not the one that fails: two threads
    /// both see an at-cap file, the first rotates and appends a line, then the second rotates that
    /// one-line file over the generation the first just saved. Both generations of history are gone and
    /// .old holds a single line -- strictly worse than the log briefly running over its cap. A failed
    /// rename is the benign half of this race and is handled separately, in TryRotate.</para>
    ///
    /// <para>Note this lock is now held across file I/O on the dispatch path. No reentrancy and no
    /// caller-supplied callback runs under it (the directory is resolved outside), so it cannot
    /// deadlock; the cost is that one slow rename or append -- antivirus, a roaming profile -- briefly
    /// stalls the other loggers, where before each was independent. Acceptable for writes this
    /// infrequent, and cheaper than losing lines.</para>
    ///
    /// <para>Cross-PROCESS this lock does nothing: two Revit instances share one local app-data
    /// directory and this is per-process. What remains there is the rotation interleave above, whose
    /// cost is bounded (a truncated .old on a debug log). The far worse cross-process failure -- a
    /// sharing violation swallowing a line outright -- is closed for every writer by the FileShare mode
    /// in Append, not by this lock. A named mutex would close the rotation half too, and is not worth
    /// its ownership and abandonment semantics for this.</para>
    /// </summary>
    private static readonly object RotationLock = new();

    /// <param name="directory">
    /// Resolved INSIDE the guard below, not by the caller, so that a throwing path computation is
    /// swallowed like every other failure here. Both call sites compute it from
    /// BrokerDiscoveryOptions.Local(), whose contract is that a logging failure never masks the
    /// exception the caller was already reporting.
    /// </param>
    internal static void Append(Func<string> directory, string fileName, string message)
    {
        try
        {
            var resolved = directory();
            Directory.CreateDirectory(resolved);
            var path = Path.Combine(resolved, fileName);

            lock (RotationLock)
            {
                TryRotate(path);

                // NOT File.AppendAllText, which opens FileShare.Read: a second writer -- another Revit
                // instance, or antivirus holding the file for an instant -- then throws a sharing
                // violation straight into the catch below, dropping the line. Losing the trace is the
                // one outcome this file cannot afford, so share with other writers explicitly. Each
                // line is a single Write, which is what keeps concurrent appends from interleaving
                // mid-line; the lock above is what makes that exact for in-process callers.
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var line = Encoding.UTF8.GetBytes($"{DateTimeOffset.UtcNow:O} {message}\n");
                stream.Write(line, 0, line.Length);
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
    private static void TryRotate(string path)
    {
        try
        {
            var existing = new FileInfo(path);
            if (!existing.Exists || existing.Length < MaxBytes)
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
