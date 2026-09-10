using MCPBridge.RevitAdapter;
using Xunit;

namespace MCPBridge.Core.Tests.RevitAdapter;

/// <summary>
/// PRD §07 v2 (issue #129): pins Win32WindowInventory.NormalizeButtonText, the pure caption-matching
/// helper the safe-failing named-button dismiss depends on. The rest of Win32WindowInventory is P/Invoke
/// and stays untested by design (see its doc comment), but this is the one piece that DECIDES whether a
/// button is the requested one, so it is worth pinning: a caption is matched with its mnemonic ampersand
/// and surrounding whitespace ignored, so a live "&amp;Cancel" or " Cancel " still resolves to "Cancel".
/// </summary>
public class Win32WindowInventoryTests
{
    [Theory]
    [InlineData("Cancel", "Cancel")]
    [InlineData("&Cancel", "Cancel")]
    [InlineData(" Cancel ", "Cancel")]
    [InlineData("  &Cancel\t", "Cancel")]
    [InlineData("Do &not save and set reminder intervals", "Do not save and set reminder intervals")]
    [InlineData("", "")]
    [InlineData("&", "")]
    public void NormalizeButtonText_StripsAmpersandsAndTrims(string raw, string expected)
    {
        Assert.Equal(expected, Win32WindowInventory.NormalizeButtonText(raw));
    }

    [Fact]
    public void NormalizeButtonText_MakesAcceleratedCaptionMatchThePlainLabel()
    {
        // The exact scenario the allowlist relies on: the live button reads as "&Cancel", the allowlist
        // stores "Cancel", and after normalization the two are equal (the adapter compares case-insensitively).
        Assert.Equal(
            Win32WindowInventory.NormalizeButtonText("Cancel"),
            Win32WindowInventory.NormalizeButtonText("&Cancel"));
    }
}
