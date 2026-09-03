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
}
