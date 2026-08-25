using System.Collections.Generic;
using System.Text;

namespace MCPBridge.Core.Protocol;

/// <summary>
/// Newline-delimited JSON framing (PRD §05 "Framing"): one JSON object per line,
/// chosen over LSP-style Content-Length headers for simplicity on both the C#
/// TcpClient side and the Go broker side. This buffer handles partial reads --
/// a chunk from a socket read may split a line across two Append() calls.
/// </summary>
public sealed class NdjsonLineBuffer
{
    private readonly StringBuilder _pending = new();

    /// <summary>Appends a chunk of raw text read from the socket and returns any complete lines it produced (blank lines skipped).</summary>
    public IEnumerable<string> Append(string chunk)
    {
        _pending.Append(chunk);

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
