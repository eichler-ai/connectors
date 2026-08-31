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

        loaded!.Value.Context.Dispose();
    }
}
