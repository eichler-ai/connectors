using MCPBridge.Core.Connection;
using Xunit;

namespace MCPBridge.Core.Tests.Connection;

public class UpdateAvailabilityTests
{
    [Theory]
    [InlineData("1.2.3", "1.3.0", true)] // both present, different -> update available
    [InlineData("1.2.3", "1.2.3", false)] // both present, equal -> no update
    [InlineData(null, "1.3.0", false)] // running unknown -> no update
    [InlineData("1.2.3", null, false)] // latest unknown -> no update
    [InlineData("", "1.3.0", false)] // running empty -> no update
    [InlineData("1.2.3", "", false)] // latest empty -> no update
    [InlineData("   ", "1.3.0", false)] // running whitespace-only -> no update
    [InlineData("1.2.3", "   ", false)] // latest whitespace-only -> no update
    [InlineData(null, null, false)] // both unknown -> no update
    [InlineData("dev", "v1.0.0", false)] // running the unreleased "dev" sentinel -> never claim an update, even against a real tag
    [InlineData("dev", "dev", false)] // redundant with the equality check above, but explicit: "dev" is guarded regardless of latest
    public void IsAvailable_ReturnsExpected(string? runningVersion, string? latestAvailableVersion, bool expected)
    {
        Assert.Equal(expected, UpdateAvailability.IsAvailable(runningVersion, latestAvailableVersion));
    }

    [Theory]
    [InlineData("v0.1.2", "v0.1.2")] // the tag as GitHub publishes it and broker.json carries it -- no second "v"
    [InlineData("0.1.2", "v0.1.2")] // a bare tag still gets exactly one
    [InlineData("V0.1.2", "v0.1.2")] // case-normalised
    [InlineData(" v0.1.2 ", "v0.1.2")]
    public void DisplayTag_HasExactlyOneLeadingV(string latest, string expected)
    {
        Assert.Equal(expected, UpdateAvailability.DisplayTag(latest));
    }

    // The Status window's add-in line (self-update-architecture.md §6.2, issue #209): the pointer's
    // version against the version this process loaded, remedy only when they differ.
    [Theory]
    [InlineData("v0.1.4", "v0.1.5", "v0.1.5 installed · running v0.1.4 — restart Revit to load it")] // the shim case the line exists for
    [InlineData("0.1.4", "v0.1.5", "v0.1.5 installed · running v0.1.4 — restart Revit to load it")] // bare assembly stamp still gets one "v"
    [InlineData("v0.1.5", "v0.1.5", "v0.1.5")] // agree -> single value, no remedy
    [InlineData("v0.1.5", "0.1.5", "v0.1.5")] // agree modulo the leading "v"
    [InlineData("v0.1.5", "V0.1.5", "v0.1.5")] // agree modulo case
    [InlineData("v0.1.5", null, "v0.1.5")] // no current.json (legacy flat install) -> today's display, never "restart to load"
    [InlineData("v0.1.5", "", "v0.1.5")]
    [InlineData("v0.1.5", "   ", "v0.1.5")]
    [InlineData("dev", "v0.1.5", "dev build")] // an unreleased build cannot be compared with anything
    [InlineData("dev", null, "dev build")]
    [InlineData(null, "v0.1.5", "dev build")]
    [InlineData("", null, "dev build")]
    [InlineData("v0.1.5", "local-20260904120000", "local-20260904120000 installed · running v0.1.5 — restart Revit to load it")] // a -LocalPackagePath tag is shown as written, not "vlocal-…"
    public void AddInStatusLine_ComparesLoadedWithPointer(string? running, string? pointer, string expected)
    {
        Assert.Equal(expected, UpdateAvailability.AddInStatusLine(running, pointer));
    }
}
