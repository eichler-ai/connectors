using System.IO;
using Xunit;

namespace MCPBridge.Discovery.Tests;

/// <summary>
/// Guards the whole self-skipping family in this project against going dead for a THIRD time.
///
/// <para>The pattern is <c>if (TryLoad() is null) return;</c>, and xUnit has no dynamic skip, so a test
/// that opts out reports as PASSED. That has now twice hidden the fact that nothing was running: first
/// because <c>MCPBRIDGE_REVITAPI_DLL</c> was never set anywhere, and again because it was set for the
/// interactive user while <c>prlctl exec</c> runs as SYSTEM -- which is how every agent session invokes
/// <c>dotnet test</c>. Both times the suite was green throughout, and both times it was found by
/// accident.</para>
///
/// <para>This test converts the next recurrence into a red build. It asserts the one thing that is
/// actually knowable: if Revit for this TFM's version IS installed at the standard location, then
/// <see cref="RealRevitApiLoader.TryLoad"/> must succeed. A machine without that install still skips --
/// legitimately, and that is the case the pattern exists for -- but a machine that HAS Revit can no
/// longer silently run nothing.</para>
///
/// <para>The cheaper tell, for a human: this project's duration. It went 0.7s to 10s when the loader was
/// fixed, because the real corpus finally loaded. A sub-second run here means nothing is reflecting.</para>
/// </summary>
public class RealRevitApiLoaderTests
{
    [Fact]
    public void WhenRevitIsInstalledForThisTargetFramework_TheRealApiTestsActuallyRun()
    {
        var installDir = $@"C:\Program Files\Autodesk\Revit {RealRevitApiLoader.RevitVersionForThisTargetFramework}";
        if (!Directory.Exists(installDir))
        {
            // No Revit for this TFM on this machine. Genuinely nothing to load, so the family's skip is
            // correct here -- this is the only path in which silence is the right answer.
            return;
        }

        var loaded = RealRevitApiLoader.TryLoad();

        Assert.True(
            loaded is not null,
            $"Revit is installed at '{installDir}', but RealRevitApiLoader.TryLoad() returned null -- so " +
            "every test in the RealRevitApi family is silently skipping and reporting PASSED on a machine " +
            "that can actually run them. This has happened twice before; see caveats.md.");

        using var context = loaded!.Value.Context;

        // Assert WHICH corpus was loaded, not merely that one was. This is the second half of the same
        // defect: before the TFM-derived version, the loader probed {2027, 2025} first-hit-wins, so on a
        // machine with both installed BOTH multi-target legs loaded 2027 and the net8.0 leg reported
        // results about an API it never touched. Everything was green and the suite even took longer,
        // which reads as more coverage rather than duplicated coverage.
        //
        // Checking the loaded assembly's own identity rather than the path it came from, because the path
        // is what the (previously wrong) selection logic produces -- asserting on it would just restate
        // the code's choice back to itself. RevitAPI.dll's major version is the release year minus 2000:
        // measured 25.4.60.0 for Revit 2025 and 27.0.4.0 for 2027.
        // Anchored to the RUNNING RUNTIME, not to the constant, and that distinction is the whole value of
        // this assertion. Deriving the expectation from RevitVersionForThisTargetFramework would be
        // circular -- the loader picks its path from that same constant, so the two agree by construction
        // and flipping the constant would keep the test green. Environment.Version is an independent fact
        // about which TFM leg is actually executing, so this pins the project-wide mapping itself:
        // net8.0-windows -> Revit 2025, net10.0-windows -> Revit 2027.
        //
        // It also covers the "both TFM legs ran on the same runtime" row in caveats.md, which warns that
        // RollForward can silently put both legs on one runtime: if that happened, one leg's expectation
        // would no longer match the constant it was compiled with, and the first assert below fires.
        var runtimeMajor = Environment.Version.Major;
        var expectedRevitVersion = runtimeMajor == 8 ? "2025" : "2027";

        Assert.True(
            expectedRevitVersion == RealRevitApiLoader.RevitVersionForThisTargetFramework,
            $"This leg is running on .NET {runtimeMajor}, which the project maps to Revit " +
            $"{expectedRevitVersion}, but it was compiled with RevitVersionForThisTargetFramework = " +
            $"'{RealRevitApiLoader.RevitVersionForThisTargetFramework}'. Either the mapping in " +
            "RealRevitApiLoader disagrees with the .csproj RevitVersion properties, or both TFM legs are " +
            "executing on the same runtime (see caveats.md).");

        var expectedMajor = int.Parse(expectedRevitVersion) - 2000;
        var actualMajor = loaded.Value.Assembly.GetName().Version?.Major;

        Assert.True(
            actualMajor == expectedMajor,
            $"This test assembly targets Revit {RealRevitApiLoader.RevitVersionForThisTargetFramework} " +
            $"(expecting RevitAPI major version {expectedMajor}), but the loaded RevitAPI.dll reports " +
            $"major version {actualMajor}. Both TFM legs are reflecting the same corpus, so one of them " +
            "is asserting against an API it does not target -- which looks like extra coverage and is not.");
    }
}
