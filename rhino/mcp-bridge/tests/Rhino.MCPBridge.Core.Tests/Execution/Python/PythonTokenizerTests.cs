using Rhino.MCPBridge.Core.Execution.Python;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Execution.Python;

public sealed class PythonTokenizerTests
{
    private static string Render(string text) =>
        string.Join(" ", PythonTokenizer.Tokenize(text).Select(t => t.Kind switch
        {
            PythonTokenKind.Newline => "NL",
            PythonTokenKind.String => "S(" + t.Text + ")",
            _ => t.Text,
        }));

    [Fact]
    public void NamesDotsCallsAndNewlines()
    {
        Assert.Equal("import rhinoscriptsyntax as rs NL rs . GetPoint ( S(x) ) NL NL", Render("import rhinoscriptsyntax as rs\nrs.GetPoint(\"x\")\n"));
    }

    [Fact]
    public void CommentsAreDropped_AndStringsKeepTheirValue()
    {
        Assert.Equal("x = S(doc.Undo()) NL NL", Render("x = 'doc.Undo()' # doc.Undo() here\n"));
    }

    [Theory]
    [InlineData("r'a\\nb'", "a\\nb")]
    [InlineData("'a\\nb'", "a\nb")]
    [InlineData("b'x'", "x")]
    [InlineData("f\"{y}\"", "{y}")]
    [InlineData("rb'\\d'", "\\d")]
    [InlineData("\"\"\"multi\nline\"\"\"", "multi\nline")]
    [InlineData("'it\\'s'", "it's")]
    public void StringPrefixesAndEscapes(string literal, string value)
    {
        var tokens = PythonTokenizer.Tokenize(literal);
        Assert.Equal(PythonTokenKind.String, tokens[0].Kind);
        Assert.Equal(value, tokens[0].Text);
    }

    [Fact]
    public void TripleQuotedStringsAdvanceTheLineCount()
    {
        var tokens = PythonTokenizer.Tokenize("s = '''a\nb\nc'''\nx = 1\n");
        Assert.Equal(4, tokens.First(t => t.IsName("x")).Line);
    }

    [Fact]
    public void NewlinesInsideBracketsAndAfterBackslashAreJoined()
    {
        Assert.Equal("f ( a , b ) NL x = 1 + 2 NL NL", Render("f(a,\n  b)\nx = 1 + \\\n 2\n"));
    }

    [Fact]
    public void MultiCharOperators()
    {
        Assert.Equal("a **= 2 NL b := c != d NL NL", Render("a **= 2\nb := c != d\n"));
    }

    [Fact]
    public void UnterminatedStringDoesNotThrow()
    {
        var tokens = PythonTokenizer.Tokenize("x = 'oops\ny = 2");
        Assert.Contains(tokens, t => t.IsName("y"));
    }

    [Fact]
    public void ANameThatLooksLikeAStringPrefixIsAName()
    {
        Assert.Equal("rb = 1 NL u ( 2 ) NL NL", Render("rb = 1\nu(2)\n"));
    }
}
