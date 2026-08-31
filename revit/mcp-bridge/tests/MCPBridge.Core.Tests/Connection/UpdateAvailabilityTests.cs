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
    public void IsAvailable_ReturnsExpected(string? runningVersion, string? latestAvailableVersion, bool expected)
    {
        Assert.Equal(expected, UpdateAvailability.IsAvailable(runningVersion, latestAvailableVersion));
    }
}
