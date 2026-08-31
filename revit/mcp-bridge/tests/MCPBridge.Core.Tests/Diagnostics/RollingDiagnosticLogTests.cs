using System;
using System.IO;
using System.Linq;
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

    [Fact]
    public void AppendCreatesTheDirectoryAndWritesATimestampedLine()
    {
        Assert.False(Directory.Exists(_directory));

        RollingDiagnosticLog.Append(_directory, "connection.log", "broker discovery failed");

        var line = Assert.Single(File.ReadAllLines(Path_("connection.log")));
        Assert.EndsWith(" broker discovery failed", line, StringComparison.Ordinal);

        // The timestamp is what makes an append-only diagnostic file readable at all; pin that it is a
        // real round-trippable instant, not just some prefix.
        var stamp = line[..line.IndexOf(' ', StringComparison.Ordinal)];
        Assert.True(DateTimeOffset.TryParse(stamp, out var parsed), $"not a timestamp: '{stamp}'");
        Assert.True(DateTimeOffset.UtcNow - parsed < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void AppendUsesTheDefaultCapWhenNoneIsGiven()
    {
        // Covers the parameterless overload the production call sites actually use, without asserting
        // the constant's value back at itself: three writes, well under any sane cap, must accumulate.
        RollingDiagnosticLog.Append(_directory, "startup-errors.log", "one");
        RollingDiagnosticLog.Append(_directory, "startup-errors.log", "two");
        RollingDiagnosticLog.Append(_directory, "startup-errors.log", "three");

        var lines = File.ReadAllLines(Path_("startup-errors.log"));
        Assert.Equal(3, lines.Length);
        Assert.False(File.Exists(Path_("startup-errors.log.old")));
    }

    [Fact]
    public void AppendDoesNotRotateWhileUnderTheCap()
    {
        // Deliberately writes right up to one byte short of the cap. If the comparison were `<=` rather
        // than `<`, or the cap were read as "lines" rather than bytes, this would rotate and fail.
        Directory.CreateDirectory(_directory);
        var payload = new string('x', 99);
        File.WriteAllText(Path_("connection.log"), payload);

        RollingDiagnosticLog.Append(_directory, "connection.log", "still fits", maxBytes: 100);

        Assert.False(File.Exists(Path_("connection.log.old")));
        Assert.StartsWith(payload, File.ReadAllText(Path_("connection.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void AppendRotatesOnceTheFileHasReachedTheCap()
    {
        // Exactly at the cap, not past it: a `>` instead of `>=` survives an "obviously oversize" file
        // but fails here, and that off-by-one is the whole difference between a bounded and an
        // unbounded log at the boundary.
        Directory.CreateDirectory(_directory);
        var existing = new string('x', 100);
        File.WriteAllText(Path_("connection.log"), existing);

        RollingDiagnosticLog.Append(_directory, "connection.log", "after rotation", maxBytes: 100);

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
        RollingDiagnosticLog.Append(_directory, "connection.log", "first", maxBytes: 100);

        File.WriteAllText(Path_("connection.log"), new string('b', 100));
        RollingDiagnosticLog.Append(_directory, "connection.log", "second", maxBytes: 100);

        Assert.Equal(new string('b', 100), File.ReadAllText(Path_("connection.log.old")));
        Assert.EndsWith(" second", Assert.Single(File.ReadAllLines(Path_("connection.log"))), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedRotationStillWritesTheDiagnosticLine()
    {
        // Two Revit processes share this directory and can race on the rename, so a failed rotation is
        // ordinary. Losing the line would defeat the file's whole purpose (PRD §01: a swallowed failure
        // still gets a trace), so rotation is guarded separately from the append. Occupying the .old
        // path with a DIRECTORY makes File.Move fail deterministically on every platform.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_("connection.log"), new string('x', 100));
        Directory.CreateDirectory(Path_("connection.log.old"));

        RollingDiagnosticLog.Append(_directory, "connection.log", "must survive", maxBytes: 100);

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

        RollingDiagnosticLog.Append(blocked, "connection.log", "nowhere to go");

        Assert.Equal("this is a file", File.ReadAllText(blocked));
    }

    [Fact]
    public void RotationBoundsTheLogAcrossManyWrites()
    {
        // The property the issue actually asks for, stated once end-to-end rather than only as
        // boundary cases: however long the process runs, the two generations stay bounded.
        const int cap = 512;
        foreach (var i in Enumerable.Range(0, 400))
        {
            RollingDiagnosticLog.Append(_directory, "connection.log", $"connection attempt {i} failed", cap);
        }

        var live = new FileInfo(Path_("connection.log")).Length;
        var rotated = new FileInfo(Path_("connection.log.old")).Length;

        // One line may straddle the cap, hence the slack; without rotation this is ~16KB and climbing.
        Assert.InRange(live, 1, cap + 200);
        Assert.InRange(rotated, 1, cap + 200);
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }
}
