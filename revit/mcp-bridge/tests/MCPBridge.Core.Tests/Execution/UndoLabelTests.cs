using System.Collections.Generic;
using System.Linq;
using MCPBridge.Core.Execution;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

/// <summary>#146 Phase 2b: what the human reads in Revit's Undo history for a connector run.</summary>
public class UndoLabelTests
{
    private static MutationReport Report(int created = 0, int modified = 0, int deleted = 0, params (string Cat, int C, int M)[] categories)
    {
        var byCategory = new Dictionary<string, CategoryTally>();
        foreach (var (cat, c, m) in categories)
        {
            byCategory[cat] = new CategoryTally(c, m);
        }

        return new MutationReport(created, modified, deleted, byCategory, truncated: false);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromAgentLabel_BlankYieldsNull_SoTheCallerFallsThrough(string? label) =>
        Assert.Null(UndoLabel.FromAgentLabel(label));

    [Fact]
    public void FromAgentLabel_PrefixesAndKeepsTheAgentsWords() =>
        Assert.Equal("MCP: create L1 walls", UndoLabel.FromAgentLabel("create L1 walls"));

    [Fact]
    public void FromAgentLabel_DoesNotDoubleThePrefix() =>
        Assert.Equal("MCP: already prefixed", UndoLabel.FromAgentLabel("MCP: already prefixed"));

    [Fact]
    public void FromAgentLabel_FlattensControlCharactersToOneLine()
    {
        // The Undo dropdown is a menu; a newline in it is at best a blank row.
        Assert.Equal("MCP: two lines here", UndoLabel.FromAgentLabel("two\nlines\r\n\there"));
    }

    [Fact]
    public void FromAgentLabel_DropsUnicodeLineSeparatorsAndFormatCharacters()
    {
        // U+2028/U+2029 are not char.IsControl but still break a line; U+202E (bidi override) and U+200D
        // (zero-width joiner) are invisible and can make the entry render as something other than what it
        // says -- in the one UI element that exists so a person can see what the agent did.
        Assert.Equal("MCP: a b c", UndoLabel.FromAgentLabel("a\u2028b\u2029c"));
        Assert.Equal("MCP: safe text", UndoLabel.FromAgentLabel("safe\u202E \u200Dtext"));
    }

    [Fact]
    public void FromAgentLabel_CapsLength_WithAnEllipsis()
    {
        var label = UndoLabel.FromAgentLabel(new string('x', 500))!;
        Assert.Equal(UndoLabel.MaxLength, label.Length);
        Assert.EndsWith("\u2026", label);
    }

    [Fact]
    public void FromAgentLabel_NeverSplitsASurrogatePair()
    {
        var label = UndoLabel.FromAgentLabel(string.Concat(Enumerable.Repeat("\U0001F600", 200)))!;
        Assert.True(label.Length <= UndoLabel.MaxLength);
        Assert.False(char.IsHighSurrogate(label[label.Length - 2]), "a lone high surrogate was left before the ellipsis");
        Assert.EndsWith("\u2026", label);
    }

    [Fact]
    public void FromReport_NullReport_YieldsNull() => Assert.Null(UndoLabel.FromReport(null));

    [Fact]
    public void FromReport_NamesTheSoleCategory()
    {
        Assert.Equal("MCP: 12 Walls created", UndoLabel.FromReport(Report(created: 12, categories: ("Walls", 12, 0))));
    }

    [Fact]
    public void FromReport_FallsBackToElements_WhenCategoriesAreMixed()
    {
        Assert.Equal("MCP: 3 elements created", UndoLabel.FromReport(Report(created: 3, categories: new[] { ("Walls", 2, 0), ("Doors", 1, 0) })));
    }

    [Fact]
    public void FromReport_ListsEveryKindOfChange_InCreatedModifiedDeletedOrder()
    {
        // Category names are plural in Revit, so a count of one is phrased around "element".
        var report = Report(created: 2, modified: 1, deleted: 4, categories: new[] { ("Levels", 2, 1) });
        Assert.Equal("MCP: 2 Levels created, 1 element modified (Levels), 4 elements deleted", UndoLabel.FromReport(report));
    }

    [Fact]
    public void FromReport_DoesNotNameTheUncategorisedBucketAsACategory()
    {
        Assert.Equal("MCP: 3 elements created", UndoLabel.FromReport(Report(created: 3, categories: ("(none)", 3, 0))));
    }

    [Fact]
    public void FromReport_SingularElement()
    {
        Assert.Equal("MCP: 1 element deleted", UndoLabel.FromReport(Report(deleted: 1)));
    }
}
