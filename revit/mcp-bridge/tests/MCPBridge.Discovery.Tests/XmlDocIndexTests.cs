using System;
using System.IO;
using MCPBridge.Core.Discovery;
using Xunit;

namespace MCPBridge.Discovery.Tests;

/// <summary>
/// Parses Fixtures/SampleDocs.xml -- a small, hand-authored XML-doc sidecar (not compiler-generated) whose
/// doc-id strings were computed by hand against the same convention <see cref="XmlDocId"/> implements --
/// verifying <see cref="XmlDocIndex"/>'s own parsing (summary/param/returns extraction, whitespace
/// normalization) independent of whether XmlDocId's computation happens to agree.
/// </summary>
public class XmlDocIndexTests
{
    private static string SampleDocsPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SampleDocs.xml");

    [Fact]
    public void LoadFromFile_ParsesTypeSummary()
    {
        var index = XmlDocIndex.LoadFromFile(SampleDocsPath);

        Assert.True(index.TryGet("T:Sample.Widget", out var entry));
        Assert.Equal("A sample widget type, for XmlDocIndex parsing tests.", entry.Summary);
    }

    [Fact]
    public void LoadFromFile_ParsesConstructorParam()
    {
        var index = XmlDocIndex.LoadFromFile(SampleDocsPath);

        Assert.True(index.TryGet("M:Sample.Widget.#ctor(System.Int32)", out var entry));
        Assert.Equal("Creates a widget with the given id.", entry.Summary);
        Assert.Equal("The widget's id.", entry.Parameters["id"]);
    }

    [Fact]
    public void LoadFromFile_NormalizesMultiLineWhitespace()
    {
        var index = XmlDocIndex.LoadFromFile(SampleDocsPath);

        Assert.True(index.TryGet("M:Sample.Widget.Describe(System.Int32)", out var entry));
        Assert.Equal("Describes the widget across multiple lines of indented text.", entry.Summary);
        Assert.Equal("How much detail to include.", entry.Parameters["detailLevel"]);
        Assert.Equal("A textual description.", entry.Returns);
    }

    [Fact]
    public void LoadFromFile_ParsesPropertySummary()
    {
        var index = XmlDocIndex.LoadFromFile(SampleDocsPath);

        Assert.True(index.TryGet("P:Sample.Widget.Id", out var entry));
        Assert.Equal("The widget's id.", entry.Summary);
    }

    [Fact]
    public void LoadFromFile_UnknownMember_ReturnsFalse()
    {
        var index = XmlDocIndex.LoadFromFile(SampleDocsPath);

        Assert.False(index.TryGet("M:Sample.Widget.NoSuchMethod", out _));
    }

    [Fact]
    public void LoadFromFile_MissingFile_DegradesToEmpty()
    {
        var index = XmlDocIndex.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "does-not-exist.xml"));

        Assert.False(index.TryGet("T:Sample.Widget", out _));
    }

    [Fact]
    public void LoadFromFile_MalformedXml_DegradesToEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
        File.WriteAllText(path, "<doc><members><member name=\"T:X\"><summary>unterminated");
        try
        {
            var index = XmlDocIndex.LoadFromFile(path);
            Assert.False(index.TryGet("T:X", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
