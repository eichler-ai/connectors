using MCPBridge.Core.Execution;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

/// <summary>
/// PRD §07 v2: pins the auto-dismiss ALLOWLIST predicate. Only an exact (class, title) match is
/// dismissed -- Win32 class names and titles are exact OS strings, so the match is case-sensitive and
/// literal, and everything else (a "close any modal" heuristic) is explicitly out of scope.
/// </summary>
public class DialogAutoDismissPolicyTests
{
    [Fact]
    public void ShouldDismiss_ExactAllowlistedSignature_ReturnsTrue()
    {
        Assert.True(DialogAutoDismissPolicy.ShouldDismiss("#32770", "Virtual Memory - High Usage"));
    }

    [Theory]
    // wrong title, right class
    [InlineData("#32770", "Some Other Dialog")]
    [InlineData("#32770", "Virtual Memory")]
    // right title, wrong class
    [InlineData("Dialog", "Virtual Memory - High Usage")]
    [InlineData("#32771", "Virtual Memory - High Usage")]
    // case variations -- exact, case-sensitive match only
    [InlineData("#32770", "virtual memory - high usage")]
    [InlineData("#32770", "VIRTUAL MEMORY - HIGH USAGE")]
    // whitespace / empty
    [InlineData("#32770", "")]
    [InlineData("", "Virtual Memory - High Usage")]
    [InlineData("", "")]
    [InlineData("#32770", " Virtual Memory - High Usage")]
    public void ShouldDismiss_AnythingButTheExactSignature_ReturnsFalse(string className, string title)
    {
        Assert.False(DialogAutoDismissPolicy.ShouldDismiss(className, title));
    }
}
