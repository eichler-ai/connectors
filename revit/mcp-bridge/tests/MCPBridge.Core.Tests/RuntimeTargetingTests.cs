using System;
using Xunit;

namespace MCPBridge.Core.Tests;

/// <summary>
/// Asserts that each target framework's tests actually EXECUTE on that framework's runtime.
///
/// <para>
/// This exists because they once didn't, silently. The project multi-targets net8.0-windows and
/// net10.0-windows (PRD §11), but the dev VM initially had only the .NET 10 runtime, so
/// <c>RollForward=LatestMajor</c> was added to let the net8-compiled assemblies run at all. The
/// result looked like double the assurance — "274 tests passing on both TFMs" — while in fact
/// running .NET 10's <c>System.Text.Json</c> twice. That matters more here than almost anywhere:
/// <c>WireEnumNameConverter</c> exists SPECIFICALLY because net8's BCL lacks .NET 9+'s
/// <c>JsonStringEnumMemberNameAttribute</c>, so System.Text.Json is the one component whose
/// net8/net10 divergence the whole multi-target is meant to cover, and it was the one component
/// the runs could not distinguish.
/// </para>
///
/// <para>
/// A coverage claim that can't fail isn't coverage. With the .NET 8 runtime now installed and
/// <c>RollForward</c> removed, this test makes the claim self-verifying: reintroduce
/// <c>RollForward</c>, or run on a machine lacking the .NET 8 runtime, and the net8 leg fails here
/// with a message saying exactly what happened, instead of passing while testing the wrong runtime.
/// </para>
/// </summary>
public class RuntimeTargetingTests
{
    [Fact]
    public void TestsRunOnTheRuntimeMatchingTheirTargetFramework()
    {
#if NET8_0_OR_GREATER && !NET9_0_OR_GREATER
        const int expectedMajor = 8;
        const string tfm = "net8.0-windows";
#elif NET10_0_OR_GREATER
        const int expectedMajor = 10;
        const string tfm = "net10.0-windows";
#else
        const int expectedMajor = -1;
        const string tfm = "(unrecognised)";
#endif

        Assert.True(
            expectedMajor > 0,
            $"This test doesn't recognise the target framework it was compiled for ({tfm}). Add a branch " +
            "for it rather than deleting the assertion, or the multi-target coverage claim goes unchecked again.");

        var actualMajor = Environment.Version.Major;

        Assert.True(
            actualMajor == expectedMajor,
            $"Compiled for {tfm} but executing on .NET {Environment.Version} " +
            $"({System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}). " +
            "The two TFMs are then running the same runtime, so 'passing on both legs' would be " +
            "testing System.Text.Json once, not twice — which is the entire divergence this project " +
            "multi-targets for. Likely causes: a RollForward property was reintroduced in the csproj, " +
            $"or this machine has no .NET {expectedMajor}.x runtime installed (check `dotnet --list-runtimes`; " +
            "note a runtime installed to C:\\Program Files\\dotnet is invisible to an SDK rooted elsewhere, " +
            "because multi-level lookup was removed in .NET 7+).");
    }
}
