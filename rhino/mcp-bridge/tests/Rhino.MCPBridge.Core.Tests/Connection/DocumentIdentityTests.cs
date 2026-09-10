using Rhino.MCPBridge.Core.Connection;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Connection;

/// <summary>PRD §12: the identity table, pinned per row.</summary>
public sealed class DocumentIdentityTests
{
    [Fact]
    public void SavedPath_IsPrefixedAndStable()
    {
        var a = DocumentIdentity.ForSavedPath("/Users/x/Tower.3dm", caseInsensitive: true);
        var b = DocumentIdentity.ForSavedPath("/Users/x/Tower.3dm", caseInsensitive: true);
        Assert.StartsWith("doc-", a);
        Assert.Equal(16, a.Length);
        Assert.Equal(a, b);
    }

    [Fact]
    public void SavedPath_CaseFoldsOnlyWhereTheOsDoes()
    {
        var lower = DocumentIdentity.ForSavedPath("/Users/x/tower.3dm", caseInsensitive: true);
        var upper = DocumentIdentity.ForSavedPath("/Users/x/TOWER.3dm", caseInsensitive: true);
        Assert.Equal(lower, upper);
        Assert.NotEqual(DocumentIdentity.ForSavedPath("/x/a.3dm", false), DocumentIdentity.ForSavedPath("/x/A.3dm", false));
    }

    [Fact]
    public void SavedPath_SeparatorsAreNormalised()
    {
        Assert.Equal(DocumentIdentity.ForSavedPath(@"C:\p\a.3dm", true), DocumentIdentity.ForSavedPath("C:/p/a.3dm", true));
    }

    [Fact]
    public void Unsaved_IsSessionScoped_ByProcessSalt()
    {
        var salt1 = Guid.NewGuid(); var salt2 = Guid.NewGuid();
        var a = DocumentIdentity.ForUnsaved(salt1, "Untitled");
        Assert.StartsWith("tmp-", a);
        Assert.Equal(a, DocumentIdentity.ForUnsaved(salt1, "Untitled"));        // stable within a process
        Assert.NotEqual(a, DocumentIdentity.ForUnsaved(salt2, "Untitled"));     // distinct across Rhinos
        Assert.NotEqual(a, DocumentIdentity.ForUnsaved(salt1, "Untitled 2"));   // Rhino uniquifies titles
    }

    [Fact]
    public void Grasshopper_SharesTheHashWithADifferentPrefix()
    {
        var gh = DocumentIdentity.ForGrasshopperPath("/x/def.gh", true);
        Assert.StartsWith("gh-", gh);
        Assert.Equal(DocumentIdentity.ForSavedPath("/x/def.gh", true).Substring(4), gh.Substring(3));
    }
}
