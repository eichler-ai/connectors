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
        Assert.Equal("rs.AddCircle(plane_or_center, radius)", addCircle.PythonCall); // the rs.* Python call shape
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

    // ----- parser edge cases (review #297, #4/#5): the line/regex scanner must not mis-index a def-like
    // line that is actually inside a string, must keep class methods out, must survive *args/**kwargs and
    // CRLF, and must not re-trip on a function's own docstring. -----

    [Fact]
    public void ParseModule_DefInsideAModuleDocstring_IsNotIndexed()
    {
        // A module-level triple-quoted string whose body contains a column-0 `def` line. The scanner must
        // recognise it as string content, not a function (else rs.NotAFunction becomes a phantom an agent
        // could try to call).
        var src = "\"\"\"\ndef NotAFunction(x):\n    pass\n\"\"\"\n\ndef RealOne(x):\n    \"\"\"A real function.\"\"\"\n    return x\n";
        var members = RhinoScriptIndexer.ParseModule(src).ToList();
        Assert.Equal(new[] { "RealOne" }, members.Select(m => m.Name).ToArray());
    }

    [Fact]
    public void ParseModule_DefInsideAFunctionDocstring_IsNotIndexed()
    {
        // The docstring of a real function contains an example that begins with `def`. The function is
        // indexed once; the example line inside its docstring is not a second function.
        var src = "def AddThing(x):\n    \"\"\"Adds a thing.\n\n    Example:\n    def Helper(y):\n        return y\n    \"\"\"\n    return x\n";
        var members = RhinoScriptIndexer.ParseModule(src).ToList();
        Assert.Equal(new[] { "AddThing" }, members.Select(m => m.Name).ToArray());
    }

    [Fact]
    public void ParseModule_IndentedAndNestedDefs_AreExcluded()
    {
        // Only column-0 defs are module-level functions; a class method (indented) is not part of the
        // rhinoscriptsyntax surface an agent addresses as rs.*.
        var src = "class Foo:\n    def Method(self):\n        pass\n\ndef TopLevel():\n    def Inner():\n        pass\n    return 1\n";
        var members = RhinoScriptIndexer.ParseModule(src).ToList();
        Assert.Equal(new[] { "TopLevel" }, members.Select(m => m.Name).ToArray());
    }

    [Fact]
    public void ParseModule_VarargsAndKwargs_AreCapturedInTheSignature()
    {
        var src = "def DoMany(first, *args, **kwargs):\n    \"\"\"Does many things.\"\"\"\n    pass\n";
        var m = Assert.Single(RhinoScriptIndexer.ParseModule(src).ToList());
        Assert.Equal("DoMany(first, *args, **kwargs)", m.Signature);
    }

    [Fact]
    public void ParseModule_CrlfLineEndings_ParseTheSameAsLf()
    {
        var lf = "def AddThing(x):\n    \"\"\"Adds a thing.\"\"\"\n    return x\n";
        var crlf = lf.Replace("\n", "\r\n");
        var fromLf = Assert.Single(RhinoScriptIndexer.ParseModule(lf).ToList());
        var fromCrlf = Assert.Single(RhinoScriptIndexer.ParseModule(crlf).ToList());
        Assert.Equal(fromLf.Signature, fromCrlf.Signature);
        Assert.Equal("Adds a thing.", fromCrlf.Summary);
    }

    [Fact]
    public void Index_ContentHash_TracksContent_NotSizeOrMtime()
    {
        // review #297, #6: the hash must change when the bytes change (even at equal length) and must NOT
        // depend on mtime, so a touch does not force a re-index and an equal-length edit is not missed.
        var dir = Path.Combine(Path.GetTempPath(), "rs-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "mod.py");
            File.WriteAllText(file, "def AddCircleAAA(x):\n    \"\"\"One.\"\"\"\n    pass\n");
            var h1 = RhinoScriptIndexer.Index(dir)!.Value.ContentHash;

            // Equal-length edit (same byte count), different content: hash must change.
            File.WriteAllText(file, "def AddCircleBBB(x):\n    \"\"\"One.\"\"\"\n    pass\n");
            var h2 = RhinoScriptIndexer.Index(dir)!.Value.ContentHash;
            Assert.NotEqual(h1, h2);

            // Same content, bumped mtime: hash must NOT change.
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(1));
            var h3 = RhinoScriptIndexer.Index(dir)!.Value.ContentHash;
            Assert.Equal(h2, h3);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
