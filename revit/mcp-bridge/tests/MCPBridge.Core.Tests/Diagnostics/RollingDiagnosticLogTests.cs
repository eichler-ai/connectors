using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MCPBridge.Core.Diagnostics;
using Xunit;

namespace MCPBridge.Core.Tests.Diagnostics;

/// <summary>
/// Issue #11: connection.log grew without bound. Every test here is written so that removing the guard
/// it names makes it fail -- a rotation test that still passes with rotation deleted would be worse than
/// no test, since the whole defect is "the obvious code path looks fine and the file grows anyway".
///
/// <para>Every test runs against the REAL <see cref="RollingDiagnosticLog.MaxBytes"/>, because there is
/// no cap parameter to substitute: a test-only cap is what let an earlier revision pass while a call
/// site could still weaken the production one. Seeding is sparse (<c>FileStream.SetLength</c>), so a
/// 5MB threshold costs a file-length update rather than 5MB of writes or memory.</para>
/// </summary>
public sealed class RollingDiagnosticLogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mcpbridge-rollinglog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Test cleanup only.
        }
    }

    private string Path_(string name) => Path.Combine(_directory, name);

    private Func<string> Dir => () => _directory;

    /// <summary>
    /// Creates a file of exactly <paramref name="length"/> bytes without writing them, optionally with
    /// a marker at offset 0 so one generation can be told from another after a rotation.
    /// </summary>
    private void Seed(string name, long length, string marker = "")
    {
        Directory.CreateDirectory(_directory);
        using var stream = File.Create(Path_(name));
        if (marker.Length > 0)
        {
            var bytes = Encoding.UTF8.GetBytes(marker);
            stream.Write(bytes, 0, bytes.Length);
        }

        stream.SetLength(length);
    }

    private string MarkerOf(string name)
    {
        using var stream = File.OpenRead(Path_(name));
        var buffer = new byte[16];
        var read = stream.Read(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(buffer, 0, read).TrimEnd('\0');
    }

    [Fact]
    public void AppendCreatesTheDirectoryAndWritesATimestampedLine()
    {
        Assert.False(Directory.Exists(_directory));

        RollingDiagnosticLog.Append(Dir, "connection.log", "broker discovery failed");

        var line = Assert.Single(File.ReadAllLines(Path_("connection.log")));
        Assert.EndsWith(" broker discovery failed", line, StringComparison.Ordinal);

        var stamp = line[..line.IndexOf(' ', StringComparison.Ordinal)];
        Assert.True(DateTimeOffset.TryParse(stamp, out var parsed), $"not a timestamp: '{stamp}'");
        Assert.True(DateTimeOffset.UtcNow - parsed < TimeSpan.FromMinutes(1));

        // UTC specifically, not merely parseable: a bare round-trip assertion passes just as happily
        // on local-time lines, which would then interleave with the UTC ones already in the file and
        // in .old -- the exact clock-math ambiguity redeploy-and-verify.ps1 gave up timestamps to
        // avoid. This kills ":O" -> ":s" (which drops the offset entirely) unconditionally, and
        // UtcNow -> Now on any machine not itself running in UTC. It cannot catch the latter on a
        // UTC-configured machine; saying so beats implying coverage that isn't there.
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.EndsWith("+00:00", stamp, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendDoesNotRotateOneByteBelowTheCap()
    {
        // The `<` half of the boundary. Together with AppendRotatesOnceTheFileHasReachedTheCap this
        // pins the comparison exactly: one byte under must not rotate, exactly at must.
        Seed("connection.log", RollingDiagnosticLog.MaxBytes - 1, "generation-one");

        RollingDiagnosticLog.Append(Dir, "connection.log", "still fits");

        Assert.False(File.Exists(Path_("connection.log.old")));
        Assert.Equal("generation-one", MarkerOf("connection.log"));
    }

    [Fact]
    public void AppendRotatesOnceTheFileHasReachedTheCap()
    {
        // Exactly AT the cap. This is the `<=` half of the boundary, and it is also the ONLY test of
        // the production constant's wiring: there is no cap parameter, so rotation either happens at
        // MaxBytes or the guard is broken.
        Seed("connection.log", RollingDiagnosticLog.MaxBytes, "generation-one");

        RollingDiagnosticLog.Append(Dir, "connection.log", "after rotation");

        Assert.Equal(RollingDiagnosticLog.MaxBytes, new FileInfo(Path_("connection.log.old")).Length);
        Assert.Equal("generation-one", MarkerOf("connection.log.old"));
        Assert.EndsWith(" after rotation", Assert.Single(File.ReadAllLines(Path_("connection.log"))), StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondRotationReplacesTheFirstRotatedFile()
    {
        // Without overwrite:true, File.Move throws here, TryRotate swallows it, and the live log grows
        // past its cap forever from the second rotation onward -- i.e. issue #11 comes back, just
        // later. Asserting WHICH generation is in .old (not merely that it exists) is what catches it.
        Seed("connection.log", RollingDiagnosticLog.MaxBytes, "generation-one");
        RollingDiagnosticLog.Append(Dir, "connection.log", "first");

        Seed("connection.log", RollingDiagnosticLog.MaxBytes, "generation-two");
        RollingDiagnosticLog.Append(Dir, "connection.log", "second");

        Assert.Equal("generation-two", MarkerOf("connection.log.old"));
        Assert.EndsWith(" second", Assert.Single(File.ReadAllLines(Path_("connection.log"))), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedRotationStillWritesTheDiagnosticLine()
    {
        // Losing the line would defeat the file's whole purpose (PRD §01: a swallowed failure still
        // gets a trace), so rotation is guarded separately from the append. Occupying the .old path
        // with a DIRECTORY makes File.Move fail deterministically, rather than depending on a race.
        Seed("connection.log", RollingDiagnosticLog.MaxBytes, "generation-one");
        Directory.CreateDirectory(Path_("connection.log.old"));

        RollingDiagnosticLog.Append(Dir, "connection.log", "must survive");

        Assert.True(new FileInfo(Path_("connection.log")).Length > RollingDiagnosticLog.MaxBytes,
            "the line should have been appended to the oversize file rather than dropped");
        Assert.Equal("generation-one", MarkerOf("connection.log"));
    }

    [Fact]
    public void AppendSharesTheFileWithOtherWriters()
    {
        // File.AppendAllText opens FileShare.Read, so ANY concurrent holder -- another Revit instance,
        // antivirus, a human with the file open -- turns an append into a sharing violation that the
        // best-effort catch swallows, dropping the line. The lock cannot fix that; only the share mode
        // can, and this is the only test that sees it, since a same-process second writer is exactly
        // what the lock would otherwise serialize away.
        Directory.CreateDirectory(_directory);
        using var otherWriter = new FileStream(
            Path_("connection.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);

        RollingDiagnosticLog.Append(Dir, "connection.log", "written while another writer holds the file");

        otherWriter.Dispose();
        Assert.Contains("written while another writer holds the file",
            File.ReadAllText(Path_("connection.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void AppendSwallowsAnUnusableDirectory()
    {
        // The outer guard. A diagnostic writer that throws would propagate out of the reconnect loop's
        // catch blocks and take down the very thread it was reporting on.
        var blocked = Path.Combine(_directory, "not-a-directory");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(blocked, "this is a file");

        RollingDiagnosticLog.Append(() => blocked, "connection.log", "nowhere to go");

        Assert.Equal("this is a file", File.ReadAllText(blocked));
    }

    [Fact]
    public void AppendSwallowsAThrowingDirectoryResolution()
    {
        // The directory is resolved INSIDE the guard, not by the caller. Both call sites compute it
        // from BrokerDiscoveryOptions.Local(), and MCPBridgeApplication.TryLogDiagnostic's contract is
        // that a logging failure never masks the exception OnStartup was already reporting -- which
        // stops being true the moment the path computation happens one frame outside the try.
        RollingDiagnosticLog.Append(
            () => throw new InvalidOperationException("no app-data path on this machine"),
            "connection.log",
            "swallowed");
    }

    [Fact]
    public void RotationBoundsTheLogAcrossManyWrites()
    {
        // The property the issue actually asks for, end-to-end rather than only as boundary cases: a
        // process that keeps logging crosses the cap and comes back down, instead of growing forever.
        Seed("connection.log", RollingDiagnosticLog.MaxBytes - 300, "generation-one");

        foreach (var i in Enumerable.Range(0, 20))
        {
            RollingDiagnosticLog.Append(Dir, "connection.log", $"connection attempt {i} failed");
        }

        // Without rotation the live file would still be MaxBytes + ~20 lines and climbing.
        Assert.InRange(new FileInfo(Path_("connection.log")).Length, 1, 4096);
        Assert.Equal("generation-one", MarkerOf("connection.log.old"));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public void ConcurrentAppendsDoNotSilentlyDropOrCorruptLines()
    {
        // Real callers contend here inside ONE Revit process: the reconnect loop logs from its worker
        // thread, SyncDiscoveryCache from a Timer thread, ExecutionAuditTrail's retention sweep from a
        // Task.Run, and RequestDispatcher's auditTrailTrace from whichever thread is dispatching.
        //
        // Well under MaxBytes, so rotation stays out of this test and a missing line can only mean a
        // lost write. Rotation's own concurrency hazard -- two threads both seeing an at-cap file, the
        // second rotating the first's one-line log over the saved generation -- is argued in
        // RollingDiagnosticLog's comments and closed by the same lock, but it is NOT pinned here: a
        // stress test for it passed with the lock removed, so it would have been a test that could not
        // fail. Better to say the coverage stops here than to imply it doesn't.
        const int workers = 4;
        const int perWorker = 100;

        Parallel.For(0, workers, worker =>
        {
            foreach (var i in Enumerable.Range(0, perWorker))
            {
                RollingDiagnosticLog.Append(Dir, "connection.log", $"worker {worker} line {i}");
            }
        });

        var lines = File.ReadAllLines(Path_("connection.log"));
        Assert.Equal(workers * perWorker, lines.Length);

        // Fully anchored, including the timestamp: an unanchored tail match would accept a line that
        // lost its prefix or carries a spliced fragment ahead of the message, which is precisely the
        // corruption a torn concurrent write produces. And distinctness, because one duplicate plus
        // one dropped line cancel out in the count above.
        Assert.All(lines, line => Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\S+\+00:00 worker \d+ line \d+$", line));
        Assert.Equal(lines.Length, lines.Distinct(StringComparer.Ordinal).Count());
    }
}
