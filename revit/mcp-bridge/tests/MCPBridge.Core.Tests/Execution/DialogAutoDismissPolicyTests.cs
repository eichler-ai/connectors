using MCPBridge.Core.Execution;
using MCPBridge.RevitAdapter;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

/// <summary>
/// PRD §07 v2 (issue #129): pins the auto-dismiss ALLOWLIST and its per-signature action. Only an exact
/// (class, title) match is dismissed -- Win32 class names and titles are exact OS strings, so the match is
/// case-sensitive and literal, and a "close any modal" heuristic is explicitly out of scope. Each match
/// resolves to the action that is SAFE for that specific dialog: WM_CLOSE for an informational warning, a
/// named-button click for one whose other buttons mutate persistent Revit settings.
/// </summary>
public class DialogAutoDismissPolicyTests
{
    [Fact]
    public void Resolve_MemoryWarning_PostsClose()
    {
        var action = DialogAutoDismissPolicy.Resolve("#32770", "Virtual Memory - High Usage");

        Assert.NotNull(action);
        Assert.Equal(DialogDismissKind.PostClose, action!.Kind);
        Assert.Null(action.ButtonText);
    }

    [Fact]
    public void Resolve_ProjectNotSavedRecently_ClicksCancelOnly()
    {
        // The safety-critical entry: three of this dialog's four buttons mutate persistent state (save, or
        // change the reminder interval), so it must dismiss by clicking Cancel specifically -- never a
        // blanket WM_CLOSE that could resolve to a mutating default.
        var action = DialogAutoDismissPolicy.Resolve("#32770", "Project Not Saved Recently");

        Assert.NotNull(action);
        Assert.Equal(DialogDismissKind.ClickButton, action!.Kind);
        Assert.Equal("Cancel", action.ButtonText);
    }

    [Theory]
    // The Autodesk trial banner: an explicit NON-entry (#129) -- it does not wedge the instance, so it is
    // deliberately left alone. Assert it stays un-dismissed so the non-entry can't silently regress.
    [InlineData("#32770", "24 DAYS LEFT")]
    [InlineData("#32770", "Autodesk")]
    // wrong title, right class
    [InlineData("#32770", "Some Other Dialog")]
    [InlineData("#32770", "Virtual Memory")]
    // right title, wrong class
    [InlineData("Dialog", "Virtual Memory - High Usage")]
    [InlineData("#32771", "Virtual Memory - High Usage")]
    [InlineData("Dialog", "Project Not Saved Recently")]
    // case variations -- exact, case-sensitive match only
    [InlineData("#32770", "virtual memory - high usage")]
    [InlineData("#32770", "VIRTUAL MEMORY - HIGH USAGE")]
    [InlineData("#32770", "project not saved recently")]
    // whitespace / empty
    [InlineData("#32770", "")]
    [InlineData("", "Virtual Memory - High Usage")]
    [InlineData("", "")]
    [InlineData("#32770", " Virtual Memory - High Usage")]
    [InlineData("#32770", "Project Not Saved Recently ")]
    public void Resolve_AnythingButAnExactAllowlistedSignature_ReturnsNull(string className, string title)
    {
        Assert.Null(DialogAutoDismissPolicy.Resolve(className, title));
    }
}
