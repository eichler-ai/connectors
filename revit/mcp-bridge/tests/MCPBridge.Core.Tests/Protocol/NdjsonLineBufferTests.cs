using System.Linq;
using MCPBridge.Core.Protocol;
using Xunit;

namespace MCPBridge.Core.Tests.Protocol;

public class NdjsonLineBufferTests
{
    [Fact]
    public void SingleCompleteLine_YieldsOneMessage()
    {
        var buffer = new NdjsonLineBuffer();
        var lines = buffer.Append("{\"a\":1}\n").ToList();

        Assert.Single(lines);
        Assert.Equal("{\"a\":1}", lines[0]);
    }

    [Fact]
    public void PartialLine_YieldsNothingUntilNewlineArrives()
    {
        var buffer = new NdjsonLineBuffer();

        var firstChunk = buffer.Append("{\"a\":").ToList();
        Assert.Empty(firstChunk);

        var secondChunk = buffer.Append("1}\n").ToList();
        Assert.Single(secondChunk);
        Assert.Equal("{\"a\":1}", secondChunk[0]);
    }

    [Fact]
    public void MultipleLinesInOneChunk_YieldsAllOfThem()
    {
        var buffer = new NdjsonLineBuffer();
        var lines = buffer.Append("{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n").ToList();

        Assert.Equal(new[] { "{\"a\":1}", "{\"b\":2}", "{\"c\":3}" }, lines);
    }

    [Fact]
    public void BlankLines_AreSkipped()
    {
        var buffer = new NdjsonLineBuffer();
        var lines = buffer.Append("\n{\"a\":1}\n\n").ToList();

        Assert.Single(lines);
        Assert.Equal("{\"a\":1}", lines[0]);
    }

    [Fact]
    public void CarriageReturn_IsTrimmedFromLineEnd()
    {
        var buffer = new NdjsonLineBuffer();
        var lines = buffer.Append("{\"a\":1}\r\n").ToList();

        Assert.Single(lines);
        Assert.Equal("{\"a\":1}", lines[0]);
    }

    [Fact]
    public void Encode_AppendsSingleTrailingNewline()
    {
        var encoded = NdjsonLineBuffer.Encode("{\"a\":1}");
        Assert.Equal("{\"a\":1}\n", encoded);
    }
}
