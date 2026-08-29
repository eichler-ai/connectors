using System.Linq;
using System.Text;
using MCPBridge.Core.Protocol;
using Xunit;

namespace MCPBridge.Core.Tests.Protocol;

public class NdjsonLineBufferTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void SingleCompleteLine_YieldsOneMessage()
    {
        var buffer = new NdjsonLineBuffer();
        var lines = buffer.Append(Utf8("{\"a\":1}\n")).ToList();

        Assert.Single(lines);
        Assert.Equal("{\"a\":1}", lines[0]);
    }

    [Fact]
    public void PartialLine_YieldsNothingUntilNewlineArrives()
    {
        var buffer = new NdjsonLineBuffer();

        var firstChunk = buffer.Append(Utf8("{\"a\":")).ToList();
        Assert.Empty(firstChunk);

        var secondChunk = buffer.Append(Utf8("1}\n")).ToList();
        Assert.Single(secondChunk);
        Assert.Equal("{\"a\":1}", secondChunk[0]);
    }

    [Fact]
    public void MultipleLinesInOneChunk_YieldsAllOfThem()
    {
        var buffer = new NdjsonLineBuffer();
        var lines = buffer.Append(Utf8("{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n")).ToList();

        Assert.Equal(new[] { "{\"a\":1}", "{\"b\":2}", "{\"c\":3}" }, lines);
    }

    [Fact]
    public void BlankLines_AreSkipped()
    {
        var buffer = new NdjsonLineBuffer();
        var lines = buffer.Append(Utf8("\n{\"a\":1}\n\n")).ToList();

        Assert.Single(lines);
        Assert.Equal("{\"a\":1}", lines[0]);
    }

    [Fact]
    public void CarriageReturn_IsTrimmedFromLineEnd()
    {
        var buffer = new NdjsonLineBuffer();
        var lines = buffer.Append(Utf8("{\"a\":1}\r\n")).ToList();

        Assert.Single(lines);
        Assert.Equal("{\"a\":1}", lines[0]);
    }

    [Fact]
    public void MultiByteUtf8Character_SplitAcrossTwoAppends_DecodesCorrectly()
    {
        // "é" (U+00E9) encodes as the two UTF-8 bytes 0xC3 0xA9. Split the chunk right between them --
        // exactly the kind of TCP read boundary that a naive per-chunk Encoding.UTF8.GetString call
        // cannot decode correctly (it would either drop the split byte(s) or emit a U+FFFD replacement
        // character), but a stateful Decoder carries the incomplete sequence across Append calls and
        // decodes it correctly once the second half arrives.
        var full = Utf8("{\"a\":\"é\"}\n");
        // full = 7B 22 61 22 3A 22 C3 A9 22 7D 0A -- split after the C3 byte (index 6 inclusive).
        var first = full[..7];
        var second = full[7..];

        var buffer = new NdjsonLineBuffer();

        var firstResult = buffer.Append(first).ToList();
        Assert.Empty(firstResult);

        var secondResult = buffer.Append(second).ToList();
        Assert.Single(secondResult);
        Assert.Equal("{\"a\":\"é\"}", secondResult[0]);
    }

    [Fact]
    public void MultiByteUtf8Character_SplitAcrossThreeAppends_DecodesCorrectly()
    {
        // A 3-byte UTF-8 sequence (e.g. U+20AC EURO SIGN = E2 82 AC) split one byte at a time across
        // three Append calls -- exercises the Decoder carrying state across more than one boundary.
        var full = Utf8("{\"a\":\"€\"}\n");
        var b1 = full[..6];
        var b2 = full[6..7];
        var b3 = full[7..];

        var buffer = new NdjsonLineBuffer();
        Assert.Empty(buffer.Append(b1));
        Assert.Empty(buffer.Append(b2));
        var result = buffer.Append(b3).ToList();

        Assert.Single(result);
        Assert.Equal("{\"a\":\"€\"}", result[0]);
    }

    [Fact]
    public void Encode_AppendsSingleTrailingNewline()
    {
        var encoded = NdjsonLineBuffer.Encode("{\"a\":1}");
        Assert.Equal("{\"a\":1}\n", encoded);
    }

    [Fact]
    public void Append_LineExceedingMaxWithoutNewline_Throws_AndResetsBuffer()
    {
        // v1 integrated review: with no cap, a peer that streamed bytes without ever sending a
        // newline grew the pending buffer without bound. The cap is test-sized here; production
        // uses DefaultMaxLineChars, which mirrors the Go broker's 64MiB per-line cap.
        var buffer = new NdjsonLineBuffer(maxLineChars: 16);

        buffer.Append(System.Text.Encoding.UTF8.GetBytes("0123456789"));
        Assert.Throws<System.InvalidOperationException>(
            () => buffer.Append(System.Text.Encoding.UTF8.GetBytes("0123456789")));

        // The buffer must be usable again after the overflow cleared it -- the connection loop
        // tears the connection down on the throw, but the object contract stays coherent.
        var lines = buffer.Append(System.Text.Encoding.UTF8.GetBytes("{\"ok\":1}\n"));
        Assert.Equal(new[] { "{\"ok\":1}" }, lines);
    }

    [Fact]
    public void Append_CompleteLinesNearTheCap_AreUnaffected()
    {
        var buffer = new NdjsonLineBuffer(maxLineChars: 16);
        var lines = buffer.Append(System.Text.Encoding.UTF8.GetBytes("0123456789012345\n0123456789012345\n"));
        Assert.Equal(2, System.Linq.Enumerable.Count(lines));
    }
}
