using System.Collections.Generic;
using System.Text;

namespace MCPBridge.Core.Protocol;

/// <summary>
/// Newline-delimited JSON framing (PRD §05 "Framing"): one JSON object per line,
/// chosen over LSP-style Content-Length headers for simplicity on both the C#
/// TcpClient side and the Go broker side. This buffer handles partial reads --
/// a chunk from a socket read may split a line across two Append() calls.
///
/// Byte-safe by design: <see cref="Append(System.ReadOnlySpan{byte})"/> takes raw bytes straight off the
/// socket and decodes them itself via a stateful <see cref="Decoder"/>, rather than requiring the caller
/// to decode bytes to a string first (a prior API shape this class had -- flagged in review as a landmine:
/// a naive per-chunk Encoding.UTF8.GetString call cannot correctly decode a multi-byte UTF-8 sequence that
/// a TCP read boundary happens to split in half, since each half is decoded independently and the split
/// byte(s) either get silently dropped or turned into U+FFFD replacement characters). A stateful Decoder
/// (built with <c>flush: false</c> on every call) carries an incomplete trailing sequence across Append
/// calls and only ever emits fully-decoded characters, so a split multi-byte character decodes correctly
/// regardless of exactly where the chunk boundary falls -- see NdjsonLineBufferTests'
/// MultiByteUtf8Character_SplitAcrossTwoAppends_DecodesCorrectly.
///
/// This class also now owns the trailing newline on <see cref="Encode"/> -- previously deliberately
/// deferred ("to the not-yet-written transport", per this class's prior review history) until something
/// actually writes NDJSON lines to a socket. BridgeHost.Start() is that transport, so the terminator lives
/// here rather than being re-added at every call site that writes a message.
/// </summary>
public sealed class NdjsonLineBuffer
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _pending = new();

    // Reused across Append calls (one NdjsonLineBuffer instance lives for a whole connection, and Append
    // is called once per socket read for that connection's lifetime) rather than allocating a fresh char[]
    // per call -- grown, never shrunk, since callers typically read into a fixed-size buffer so the
    // required size stabilizes after the first call.
    private char[] _charScratch = System.Array.Empty<char>();

    /// <summary>
    /// Appends a chunk of raw bytes read from the socket and returns any complete lines it produced
    /// (blank lines skipped, trailing \r stripped). Safe to call with a chunk that ends mid-way through a
    /// multi-byte UTF-8 sequence -- the incomplete tail is retained internally by the Decoder and combined
    /// with the next call's bytes rather than being lost or mis-decoded.
    /// </summary>
    public IEnumerable<string> Append(System.ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length > 0)
        {
            // Deliberately does NOT call Decoder.GetCharCount first to size the buffer: per Decoder's own
            // documented contract, GetCharCount is itself stateful for a multi-byte encoding like UTF-8 --
            // calling it and then GetChars on the very same chunk would process the same incomplete
            // trailing byte sequence twice, corrupting the decode (confirmed empirically: this used to
            // turn a 3-byte sequence split across two prior Append calls into replacement characters
            // instead of the real code point). A char buffer sized to the byte count is always big enough
            // for UTF-8 (decoded chars can never outnumber input bytes), so there's no need for the GetCharCount
            // call at all.
            if (_charScratch.Length < chunk.Length)
            {
                _charScratch = new char[chunk.Length];
            }

            var written = _decoder.GetChars(chunk, _charScratch, flush: false);
            if (written > 0)
            {
                _pending.Append(_charScratch, 0, written);
            }
        }

        var lines = new List<string>();
        var text = _pending.ToString();
        var searchStart = 0;

        while (true)
        {
            var newlineIndex = text.IndexOf('\n', searchStart);
            if (newlineIndex < 0)
            {
                break;
            }

            var line = text[searchStart..newlineIndex];
            if (line.EndsWith('\r'))
            {
                line = line[..^1];
            }

            if (line.Length > 0)
            {
                lines.Add(line);
            }

            searchStart = newlineIndex + 1;
        }

        _pending.Clear();
        _pending.Append(text[searchStart..]);

        return lines;
    }

    /// <summary>Encodes one JSON-RPC message as an NDJSON line (message text plus a single trailing newline).</summary>
    public static string Encode(string jsonMessage) => jsonMessage + "\n";
}
