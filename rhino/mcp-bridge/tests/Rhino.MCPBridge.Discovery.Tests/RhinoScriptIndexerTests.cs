using System;
using System.IO;
using System.Linq;
using Rhino.MCPBridge.Core.Discovery;
using Xunit;

namespace Rhino.MCPBridge.Discovery.Tests;

/// <summary>
/// The rhinoscriptsyntax source parser (PRD §09 kind=rhinoscript), over bundled fixture .py modules so it
/// runs on CI with no Rhino/CPython installed. Pins the parse of the real rhinoscript docstring shape:
/// PascalCase module-level functions indexed with summary/params/returns, and lower/underscore helpers
/// excluded.
/// </summary>
public class RhinoScriptIndexerTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "rhinoscript");

    [Fact]
    public void ParseModule_IndexesPascalCaseFunctions_AndExcludesHelpers()
    {
        var src = File.ReadAllText(Path.Combine(FixtureDir, "curve_fixture.py"));
        var members = RhinoScriptIndexer.ParseModule(src).ToList();

        Assert.Equal(new[] { "AddCircle", "AddLine" }, members.Select(m => m.Name).OrderBy(n => n).ToArray());
        Assert.DoesNotContain(members, m => m.Name is "coercecurve" or "__privatehelper");
    }

    [Fact]
    public void ParseModule_MultiLineDef_CapturesTheSignatureSummaryParamsAndReturns()
    {
        var src = File.ReadAllText(Path.Combine(FixtureDir, "curve_fixture.py"));
        var addCircle = RhinoScriptIndexer.ParseModule(src).Single(m => m.Name == "AddCircle");

        Assert.Equal("function", addCircle.Kind);
        Assert.Equal("rhinoscript:AddCircle", addCircle.MemberId);
        Assert.Equal("AddCircle(plane_or_center, radius)", addCircle.Signature); // multi-line def collapsed
        Assert.Equal("Adds a circle curve to the document", addCircle.Summary);
        Assert.Equal("guid: id of the new curve object", addCircle.Returns);
        Assert.Equal(2, addCircle.Parameters.Count);
        var p0 = addCircle.Parameters[0];
        Assert.Equal("plane_or_center", p0.Name);
        Assert.Equal("point|plane", p0.Type);
        Assert.Equal("plane on which the circle will lie", p0.Description);
    }

    [Fact]
    public void ParseModule_SingleLineDocstring_IsTheSummary_NoReturns()
    {
        var src = File.ReadAllText(Path.Combine(FixtureDir, "curve_fixture.py"));
        var addLine = RhinoScriptIndexer.ParseModule(src).Single(m => m.Name == "AddLine");

        Assert.Equal("Adds a line curve to the current model", addLine.Summary);
        Assert.Null(addLine.Returns);
    }

    [Fact]
    public void ParseModule_NoDocstring_FallsBackToSignatureParams_WithNoSummary()
    {
        var src = File.ReadAllText(Path.Combine(FixtureDir, "doc_fixture.py"));
        var enableRedraw = RhinoScriptIndexer.ParseModule(src).Single(m => m.Name == "EnableRedraw");

        Assert.Null(enableRedraw.Summary);
        Assert.Null(enableRedraw.Returns);
        // No Parameters: docstring section -> parameter names come from the def, defaults stripped.
        Assert.Equal(new[] { "enable" }, enableRedraw.Parameters.Select(p => p.Name).ToArray());
        Assert.Equal("", enableRedraw.Parameters[0].Type);
    }

    [Fact]
    public void ParseModule_ParametersSectionWithoutReturns_ParsesOptionalTypes()
    {
        var src = File.ReadAllText(Path.Combine(FixtureDir, "doc_fixture.py"));
        var getObject = RhinoScriptIndexer.ParseModule(src).Single(m => m.Name == "GetObject");

        Assert.Equal("Prompts user to pick or select a single object", getObject.Summary);
        Assert.Null(getObject.Returns);
        Assert.Equal(3, getObject.Parameters.Count);
        Assert.Equal("str, optional", getObject.Parameters[0].Type);
    }

    [Fact]
    public void Index_CollapsesAllModulesIntoOneRhinoscriptsyntaxType()
    {
        var indexed = RhinoScriptIndexer.Index(FixtureDir);
        Assert.NotNull(indexed);
        var (hash, types) = indexed!.Value;

        Assert.NotEmpty(hash);
        var type = Assert.Single(types);
        Assert.Equal("rhinoscriptsyntax", type.Namespace);
        Assert.Equal("rhinoscriptsyntax", type.FullName);
        // AddCircle, AddLine, EnableRedraw, GetObject — the four PascalCase functions; helpers excluded.
        Assert.Equal(new[] { "AddCircle", "AddLine", "EnableRedraw", "GetObject" },
            type.Members.Select(m => m.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void Index_MissingDirectory_IsNull()
    {
        Assert.Null(RhinoScriptIndexer.Index(Path.Combine(AppContext.BaseDirectory, "no-such-dir")));
        Assert.Null(RhinoScriptIndexer.Index(null));
    }
}
