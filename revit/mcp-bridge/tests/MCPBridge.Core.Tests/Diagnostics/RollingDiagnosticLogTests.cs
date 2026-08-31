using System;

using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MCPBridge.Core.Diagnostics;
using Xunit;

namespace MCPBridge.Core.Tests.Diagnostics;

/// <summary>
/// Issue #11: connection.log grew without bound. Every test here is written so that removing the guard
/// it names makes it fail -- a rotation test that still passes with rotation deleted would be worse than
/// no test, since the whole defect is "the obvious code path looks fine and the file grows anyway".
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
    public void TheDefaultOverloadRotatesAtMaxBytes()
    {
        // THE test for the default cap, and the reason it writes a real 5MB file rather than asserting
        // MaxBytes back at itself. Both production call sites use the parameterless overload, so
        // changing it to `Append(..., long.MaxValue)` -- or fat-fingering MaxBytes to 5GB -- restores
        // issue #11 in full. Every other rotation test here passes maxBytes explicitly and so cannot
        // see that: an earlier revision of this file had no coverage of the wiring at all, and an
        // independent review caught it.
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path_("connection.log"), new byte[RollingDiagnosticLog.MaxBytes]);

        RollingDiagnosticLog.Append(Dir, "connection.log", "past the default cap");

        Assert.True(File.Exists(Path_("connection.log.old")), "the default overload did not rotate at MaxBytes");
        Assert.Equal(RollingDiagnosticLog.MaxBytes, new FileInfo(Path_("connection.log.old")).Length);
        Assert.EndsWith(" past the default cap", Assert.Single(File.ReadAllLines(Path_("connection.log"))), StringComparison.Ordinal);
    }

    [Fact]
    public void AppendDoesNotRotateAFileThatIsWellUnderTheCap()
    {
        // Kills a mis-scaled threshold (maxBytes / 2, or a units mix-up) and dropping the length check
        // entirely. It does NOT catch `<` -> `<=`, nor a cap misread as a line count -- 99 bytes is
        // both under 100 bytes and under 100 lines. AppendRotatesOnceTheFileHasReachedTheCap is what
        // pins the boundary; this pins that a comfortably-small file is left alone.
        Directory.CreateDirectory(_directory);
        var payload = new string('x', 99);
        File.WriteAllText(Path_("connection.log"), payload);

        RollingDiagnosticLog.Append(Dir, "connection.log", "still fits", maxBytes: 100);

        Assert.False(File.Exists(Path_("connection.log.old")));
        Assert.StartsWith(payload, File.ReadAllText(Path_("connection.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void AppendRotatesOnceTheFileHasReachedTheCap()
    {
        // Exactly AT the cap, not past it: this is the case that separates `<` from `<=`, and that
        // off-by-one is the difference between a bounded and an unbounded log at the boundary.
        Directory.CreateDirectory(_directory);
        var existing = new string('x', 100);
        File.WriteAllText(Path_("connection.log"), existing);

        RollingDiagnosticLog.Append(Dir, "connection.log", "after rotation", maxBytes: 100);

        Assert.Equal(existing, File.ReadAllText(Path_("connection.log.old")));

        var live = File.ReadAllLines(Path_("connection.log"));
        Assert.EndsWith(" after rotation", Assert.Single(live), StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondRotationReplacesTheFirstRotatedFile()
    {
        // Without overwrite:true, File.Move throws here, TryRotate swallows it, and the live log grows
        // past its cap forever from the second rotation onward -- i.e. issue #11 comes back, just
        // later. Asserting the .old CONTENT (not merely that it exists) is what catches that.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_("connection.log"), new string('a', 100));
        RollingDiagnosticLog.Append(Dir, "connection.log", "first", maxBytes: 100);

        File.WriteAllText(Path_("connection.log"), new string('b', 100));
        RollingDiagnosticLog.Append(Dir, "connection.log", "second", maxBytes: 100);

        Assert.Equal(new string('b', 100), File.ReadAllText(Path_("connection.log.old")));
        Assert.EndsWith(" second", Assert.Single(File.ReadAllLines(Path_("connection.log"))), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedRotationStillWritesTheDiagnosticLine()
    {
        // Losing the line would defeat the file's whole purpose (PRD §01: a swallowed failure still
        // gets a trace), so rotation is guarded separately from the append. Occupying the .old path
        // with a DIRECTORY makes File.Move fail deterministically, rather than depending on a race.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_("connection.log"), new string('x', 100));
        Directory.CreateDirectory(Path_("connection.log.old"));

        RollingDiagnosticLog.Append(Dir, "connection.log", "must survive", maxBytes: 100);

        var text = File.ReadAllText(Path_("connection.log"));
        Assert.Contains("must survive", text, StringComparison.Ordinal);
        Assert.StartsWith(new string('x', 100), text, StringComparison.Ordinal);
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
        // The property the issue actually asks for, stated once end-to-end rather than only as
        // boundary cases: however long the process runs, the two generations stay bounded.
        const int cap = 512;
        foreach (var i in Enumerable.Range(0, 400))
        {
            RollingDiagnosticLog.Append(Dir, "connection.log", $"connection attempt {i} failed", cap);
        }

        var live = new FileInfo(Path_("connection.log")).Length;
        var rotated = new FileInfo(Path_("connection.log.old")).Length;

        // One line may straddle the cap, hence the slack; without rotation this is ~16KB and climbing.
        Assert.InRange(live, 1, cap + 200);
        Assert.InRange(rotated, 1, cap + 200);
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public void ConcurrentAppendsDoNotSilentlyDropLines()
    {
        // Real callers contend here inside ONE Revit process: the reconnect loop logs from its worker
        // thread, SyncDiscoveryCache from a Timer thread, and RequestDispatcher's auditTrailTrace from
        // whichever thread is dispatching. File.AppendAllText opens with FileShare.Read, so two
        // overlapping appends throw a sharing violation -- straight into the outer best-effort catch,
        // which drops the line without a trace. That is the worst possible failure for a file whose
        // entire job is that a swallowed failure still leaves one (PRD §01).
        //
        // A cap far above anything written keeps rotation out of this test, so a lost line can only
        // mean a lost write. Rotation's own concurrency hazard -- two threads both seeing an at-cap
        // file, the second rotating the first's one-line log over the saved generation -- is argued in
        // RollingDiagnosticLog's comments and closed by the same lock, but it is NOT pinned here: a
        // stress test for it passed with the lock removed, so it would have been a test that could not
        // fail. Better to say the coverage stops here than to imply it doesn't.
        const int workers = 16;
        const int perWorker = 250;

        Parallel.For(0, workers, worker =>
        {
            foreach (var i in Enumerable.Range(0, perWorker))
            {
                RollingDiagnosticLog.Append(Dir, "connection.log", $"worker {worker} line {i}", maxBytes: 100L * 1024 * 1024);
            }
        });

        var lines = File.ReadAllLines(Path_("connection.log"));
        Assert.Equal(workers * perWorker, lines.Length);
        Assert.False(File.Exists(Path_("connection.log.old")), "the cap was meant to keep rotation out of this test");

        // Every line intact, not merely the right count -- an interleaved write can also corrupt one.
        Assert.All(lines, line => Assert.Matches(@" worker \d+ line \d+$", line));
    }
}
