using System;
using System.Collections.Generic;
using System.Text;

namespace Rhino.MCPBridge.Core.Execution.Python;

internal enum PythonTokenKind
{
    Name,
    Number,
    String,
    Op,
    /// <summary>A logical line end: a newline outside brackets and not escaped by a backslash.</summary>
    Newline,
}

internal readonly record struct PythonToken(PythonTokenKind Kind, string Text, int Line)
{
    public bool Is(PythonTokenKind kind, string text) => Kind == kind && Text == text;
    public bool IsOp(string text) => Is(PythonTokenKind.Op, text);
    public bool IsName(string text) => Is(PythonTokenKind.Name, text);
}

/// <summary>
/// A flat tokenizer for Python 3 source, sufficient for <see cref="PythonScriptGuard"/>: names,
/// numbers, string literals (every prefix, single and triple quoted, with the decoded value of plain
/// literals so <c>rs.Command("_Undo")</c> can be read), operators, and logical newlines. Comments
/// are dropped; newlines inside brackets and after a backslash are joined, as Python does. It never
/// throws on malformed input -- an unterminated string runs to the end of the text -- because the
/// guard's job is to find denied members in text that CPython will parse a moment later, not to be
/// a parser.
/// </summary>
internal static class PythonTokenizer
{
    private static readonly string[] Ops3 = { "**=", "//=", ">>=", "<<=", "...", "!=" };
    private static readonly string[] Ops2 = { "**", "//", ">>", "<<", "<=", ">=", "==", "!=", "->", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", ":=", "@=" };

    public static List<PythonToken> Tokenize(string text)
    {
        var tokens = new List<PythonToken>();
        var i = 0;
        var line = 1;
        var depth = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n')
            {
                if (depth == 0)
                {
                    tokens.Add(new PythonToken(PythonTokenKind.Newline, "\n", line));
                }

                line++;
                i++;
                continue;
            }

            if (c == '\\' && i + 1 < text.Length && (text[i + 1] == '\n' || text[i + 1] == '\r'))
            {
                i += text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n' ? 3 : 2;
                line++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '#')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }

            // String literal, with an optional prefix of letters (r, b, u, f, rb, br, fr, rf ...).
            var p = i;
            while (p < text.Length && p - i < 2 && char.IsLetter(text[p])) p++;
            if (p < text.Length && (text[p] == '"' || text[p] == '\'') && (p == i || IsStringPrefix(text.AsSpan(i, p - i))))
            {
                var prefix = text.Substring(i, p - i).ToLowerInvariant();
                var start = line;
                var (value, next, newlines) = ReadString(text, p, raw: prefix.Contains('r'));
                tokens.Add(new PythonToken(PythonTokenKind.String, value, start));
                if (prefix.Contains('f'))
                {
                    // The {expressions} of an f-string are code: emit their tokens so the guard walks them.
                    foreach (var expr in FStringExpressions(value))
                    {
                        foreach (var inner in Tokenize(expr))
                        {
                            if (inner.Kind != PythonTokenKind.Newline) tokens.Add(inner with { Line = start });
                        }
                    }
                }

                line += newlines;
                i = next;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var s = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                tokens.Add(new PythonToken(PythonTokenKind.Name, text.Substring(s, i - s), line));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                var s = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.' || text[i] == '_')) i++;
                tokens.Add(new PythonToken(PythonTokenKind.Number, text.Substring(s, i - s), line));
                continue;
            }

            var op = MatchOp(text, i);
            if (op == "(" || op == "[" || op == "{") depth++;
            else if ((op == ")" || op == "]" || op == "}") && depth > 0) depth--;
            tokens.Add(new PythonToken(PythonTokenKind.Op, op, line));
            i += op.Length;
        }

        tokens.Add(new PythonToken(PythonTokenKind.Newline, "\n", line));
        return tokens;
    }

    /// <summary>The expression parts of an f-string body: the text inside each single-brace group
    /// ({{ and }} are literal braces), up to a top-level format spec or conversion.</summary>
    internal static IEnumerable<string> FStringExpressions(string body)
    {
        var i = 0;
        while (i < body.Length)
        {
            if (body[i] == '{')
            {
                if (i + 1 < body.Length && body[i + 1] == '{') { i += 2; continue; }
                var depth = 1;
                var j = i + 1;
                while (j < body.Length && depth > 0)
                {
                    if (body[j] == '{') depth++;
                    else if (body[j] == '}') depth--;
                    j++;
                }

                var expr = body.Substring(i + 1, Math.Max(0, j - i - 2));
                // Strip a trailing !r/!s conversion and :format spec at the top level.
                var cut = TopLevelIndexOf(expr, '!', ':');
                yield return cut < 0 ? expr : expr.Substring(0, cut);
                i = j;
                continue;
            }

            i++;
        }
    }

    private static int TopLevelIndexOf(string s, char a, char b)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '(' || c == '[' || c == '{') depth++;
            else if (c == ')' || c == ']' || c == '}') depth--;
            else if (depth == 0 && (c == a || c == b) && !(c == '!' && i + 1 < s.Length && s[i + 1] == '=')) return i;
        }

        return -1;
    }

    private static bool IsStringPrefix(ReadOnlySpan<char> prefix)
    {
        foreach (var ch in prefix)
        {
            if ("rRbBuUfF".IndexOf(ch) < 0) return false;
        }

        return true;
    }

    private static string MatchOp(string text, int i)
    {
        foreach (var op in Ops3)
        {
            if (string.CompareOrdinal(text, i, op, 0, op.Length) == 0) return op;
        }

        foreach (var op in Ops2)
        {
            if (string.CompareOrdinal(text, i, op, 0, op.Length) == 0) return op;
        }

        return text[i].ToString();
    }

    /// <summary>Reads a quoted literal starting at the opening quote; returns its (decoded for
    /// non-raw, simple escapes only) value, the index after the closing quote, and the newlines crossed.</summary>
    private static (string Value, int Next, int Newlines) ReadString(string text, int start, bool raw)
    {
        var quote = text[start];
        var triple = start + 2 < text.Length && text[start + 1] == quote && text[start + 2] == quote;
        var i = start + (triple ? 3 : 1);
        var sb = new StringBuilder();
        var newlines = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                var n = text[i + 1];
                if (n == '\n')
                {
                    newlines++;
                    if (raw) sb.Append(c).Append(n);
                }
                else if (raw)
                {
                    sb.Append(c).Append(n);
                }
                else
                {
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '\\': case '\'': case '"': sb.Append(n); break;
                        default: sb.Append(c).Append(n); break;
                    }
                }

                i += 2;
                continue;
            }

            if (triple)
            {
                if (c == quote && i + 2 < text.Length && text[i + 1] == quote && text[i + 2] == quote)
                {
                    return (sb.ToString(), i + 3, newlines);
                }
            }
            else if (c == quote)
            {
                return (sb.ToString(), i + 1, newlines);
            }
            else if (c == '\n')
            {
                // An unterminated single-quoted string ends at the line, as CPython's error would.
                return (sb.ToString(), i, newlines);
            }

            if (c == '\n') newlines++;
            sb.Append(c);
            i++;
        }

        return (sb.ToString(), text.Length, newlines);
    }
}
