using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Rhino.MCPBridge.Core.Discovery;

/// <summary>
/// Indexes the <c>rhinoscriptsyntax</c> Python library (PRD §09 kind=rhinoscript) into the same
/// <see cref="DiscoveryCache"/> as the reflected .NET assemblies, so an agent searching "add a circle"
/// finds <c>rs.AddCircle</c> beside <c>Rhino.Geometry.Circle</c>. Unlike <see cref="DiscoveryReflector"/>
/// (a .NET reflection engine), this is a source parser: <c>rhinoscriptsyntax</c> is a Python package
/// deployed with Rhino's CPython runtime, and its ~935 functions carry Google-style docstrings this reads
/// for summaries, parameters and returns.
///
/// <para>Every public function is a module-level, PascalCase <c>def</c> (<c>def AddCircle(...)</c>);
/// helpers are lower_snake or underscore-prefixed (<c>def coercecurve</c>, <c>def __x</c>) and are
/// excluded. All functions across all modules are collapsed into ONE synthetic type,
/// <c>rhinoscriptsyntax</c>, because that is how an agent addresses them (<c>rs.AddCircle</c>), not by the
/// internal module they happen to live in.</para>
///
/// <para>The parser is a pragmatic line/regex scanner, not a full Python parser: the files are highly
/// regular, and a malformed one is skipped rather than allowed to fail the whole index.</para>
/// </summary>
public static class RhinoScriptIndexer
{
    /// <summary>The single synthetic namespace/type all rhinoscriptsyntax functions live under.</summary>
    public const string Namespace = "rhinoscriptsyntax";

    /// <summary>The assemblies.file_path sentinel + SyncSource id for this source.</summary>
    public const string SourceId = "rhinoscriptsyntax";

    // Module-level (column 0) PascalCase def. Indented defs (class methods) and lower/underscore helpers
    // are excluded by the anchor + the [A-Z] first letter.
    private static readonly Regex DefLine = new(@"^def ([A-Z]\w*)\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// Locates the deployed rhinoscript directory (<c>&lt;home&gt;/.rhinocode/&lt;runtime&gt;/site-rhinopython/rhinoscript</c>),
    /// or null when Rhino's CPython runtime has not been deployed yet (it deploys on the first script run;
    /// see the #287 warm-up). Globs the runtime version directory so it survives a Rhino/Python update.
    /// </summary>
    public static string? FindSourceDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return null;
        }

        var rhinocode = Path.Combine(home, ".rhinocode");
        if (!Directory.Exists(rhinocode))
        {
            return null;
        }

        // Prefer the most recently written runtime if a machine has more than one (e.g. after an update).
        return Directory.EnumerateDirectories(rhinocode)
            .Select(d => Path.Combine(d, "site-rhinopython", "rhinoscript"))
            .Where(Directory.Exists)
            .OrderByDescending(d => { try { return Directory.GetLastWriteTimeUtc(d); } catch { return DateTime.MinValue; } })
            .FirstOrDefault();
    }

    /// <summary>
    /// Indexes every <c>.py</c> in <paramref name="directory"/> into a single <see cref="ReflectedType"/>.
    /// Returns null when the directory is absent/empty or yields no functions. Never throws for a bad file.
    /// </summary>
    public static (string ContentHash, IReadOnlyList<ReflectedType> Types)? Index(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.py");
        }
        catch
        {
            return null;
        }

        Array.Sort(files, StringComparer.Ordinal);
        var members = new List<ReflectedMember>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hashInput = new StringBuilder();

        foreach (var file in files)
        {
            try
            {
                // Hash the CONTENT, not size+mtime. This hash is what SyncSource compares to decide whether
                // to re-index (and, since review #297 #1, the rhinoscript row survives each launch, so the
                // comparison actually governs). size+mtime would miss an equal-length in-place edit and, worse,
                // would churn a full re-index on a mere touch -- a git checkout rewrites mtime without changing
                // a byte. Hashing the text we already read to parse is correct and costs nothing extra.
                var text = File.ReadAllText(file);
                hashInput.Append(Path.GetFileName(file)).Append('\n').Append(text).Append('\n');
                foreach (var m in ParseModule(text))
                {
                    // A duplicate name across modules (rare) keeps the first; member_id must stay unique.
                    if (seen.Add(m.Name))
                    {
                        members.Add(m);
                    }
                }
            }
            catch
            {
                // Skip an unreadable/malformed module; index the rest.
            }
        }

        if (members.Count == 0)
        {
            return null;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput.ToString())));
        var type = new ReflectedType
        {
            Namespace = Namespace,
            Name = Namespace,
            FullName = Namespace,
            MemberId = "rhinoscript:" + Namespace,
            Documented = true,
            BaseFullName = null,
            Members = members,
        };
        return (hash, new[] { type });
    }

    /// <summary>Parses one module's source into its public rhinoscript functions. Public + static so the
    /// parser is unit-testable over a string without a directory.</summary>
    public static IEnumerable<ReflectedMember> ParseModule(string source)
    {
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string? openQuote = null; // the triple-quote delimiter open at the start of the current line, or null
        for (var i = 0; i < lines.Length; i++)
        {
            // A line only holds a real def when it does not begin inside a triple-quoted string. A module
            // docstring, or a function docstring with an example like `def Foo(...)` in it, would otherwise
            // be mis-indexed as a phantom rhinoscript function an agent could try to call (review #297, #4).
            // Track the triple-quote state across lines; only triple quotes matter, since a column-0 def
            // cannot fall inside a single-line '...'/"..." string.
            var startedInString = openQuote is not null;
            openQuote = AdvanceTripleQuoteState(lines[i], openQuote);
            if (startedInString)
            {
                continue;
            }

            var match = DefLine.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups[1].Value;
            var (paramsText, afterDef) = ReadParams(lines, i);
            var (docstring, _) = ReadDocstring(lines, afterDef);
            yield return BuildMember(name, paramsText, docstring);
            // Resume after the def's signature line(s); the loop's triple-quote tracking then walks the
            // function's docstring normally and skips its body, so a def-like line inside it is ignored.
            i = afterDef;
        }
    }

    /// <summary>
    /// Advances the triple-quoted-string state across one line: given the delimiter open at the START of the
    /// line (<c>"""</c>, <c>'''</c>, or null when not inside one), returns the delimiter open at its END.
    /// Scans left to right so several quotes on one line (an open then a close, or a close then a reopen) net
    /// out. Inside an open block only a matching delimiter closes it; the other kind is literal text. A
    /// pragmatic scanner, in keeping with the rest of this parser: it does not model escapes or a <c>"""</c>
    /// embedded in a single-line string, neither of which occurs in rhinoscriptsyntax.
    /// </summary>
    private static string? AdvanceTripleQuoteState(string line, string? openQuote)
    {
        for (var c = 0; c < line.Length;)
        {
            if (openQuote is null)
            {
                if (IsTokenAt(line, c, "\"\"\"")) { openQuote = "\"\"\""; c += 3; }
                else if (IsTokenAt(line, c, "'''")) { openQuote = "'''"; c += 3; }
                else { c++; }
            }
            else if (IsTokenAt(line, c, openQuote))
            {
                openQuote = null;
                c += 3;
            }
            else
            {
                c++;
            }
        }

        return openQuote;
    }

    private static bool IsTokenAt(string s, int index, string token) =>
        index + token.Length <= s.Length && string.CompareOrdinal(s, index, token, 0, token.Length) == 0;

    /// <summary>Reads the parenthesised parameter text of a def starting at <paramref name="start"/>,
    /// following it across lines until the parens balance. Returns the collapsed params and the index of
    /// the line the def (its <c>):</c>) ended on.</summary>
    private static (string Params, int EndLine) ReadParams(string[] lines, int start)
    {
        var open = lines[start].IndexOf('(');
        var depth = 0;
        var sb = new StringBuilder();
        for (var i = start; i < lines.Length; i++)
        {
            var from = i == start ? open : 0;
            for (var c = from; c < lines[i].Length; c++)
            {
                var ch = lines[i][c];
                if (ch == '(')
                {
                    depth++;
                    if (depth == 1) continue; // skip the outermost '('
                }
                else if (ch == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return (CollapseParams(sb.ToString()), i);
                    }
                }

                if (depth >= 1)
                {
                    sb.Append(ch);
                }
            }

            sb.Append(' '); // line break inside the param list becomes a space
        }

        return (CollapseParams(sb.ToString()), lines.Length - 1);
    }

    private static string CollapseParams(string raw)
    {
        var collapsed = Regex.Replace(raw, @"\s+", " ").Trim();
        return collapsed;
    }

    /// <summary>Reads the triple-quoted docstring that begins on the first non-blank line at/after
    /// <paramref name="afterDef"/>; returns its inner text (or null if the next code line is not a
    /// docstring) and the line it ended on.</summary>
    private static (string? Text, int EndLine) ReadDocstring(string[] lines, int afterDef)
    {
        var i = afterDef + 1;
        while (i < lines.Length && lines[i].Trim().Length == 0)
        {
            i++;
        }

        if (i >= lines.Length)
        {
            return (null, afterDef);
        }

        var trimmed = lines[i].TrimStart();
        var quote = trimmed.StartsWith("\"\"\"", StringComparison.Ordinal) ? "\"\"\""
                  : trimmed.StartsWith("'''", StringComparison.Ordinal) ? "'''"
                  : null;
        if (quote is null)
        {
            return (null, afterDef); // no docstring (e.g. the no-docstring fixture)
        }

        var body = new StringBuilder();
        var afterOpen = trimmed.Substring(quote.Length);
        // Single-line docstring: """summary"""
        var closeSame = afterOpen.IndexOf(quote, StringComparison.Ordinal);
        if (closeSame >= 0)
        {
            return (afterOpen.Substring(0, closeSame), i);
        }

        body.Append(afterOpen).Append('\n');
        for (var j = i + 1; j < lines.Length; j++)
        {
            var close = lines[j].IndexOf(quote, StringComparison.Ordinal);
            if (close >= 0)
            {
                body.Append(lines[j].Substring(0, close));
                return (body.ToString(), j);
            }

            body.Append(lines[j]).Append('\n');
        }

        return (body.ToString(), lines.Length - 1);
    }

    private static ReflectedMember BuildMember(string name, string paramsText, string? docstring)
    {
        var summary = DiscoveryReflector.Truncate(FirstDocLine(docstring));
        var (docParams, returns) = ParseDocSections(docstring);
        var parameters = docParams.Count > 0 ? docParams : ParamsFromSignature(paramsText);
        return new ReflectedMember
        {
            Kind = "function",
            Name = name,
            Signature = $"{name}({paramsText})",
            // rhinoscriptsyntax is Python-only, so the Python call shape is the rs.* wrapper an agent
            // actually writes; there is no C# form (PRD §09 "rs. wrapper where one exists").
            PythonCall = $"rs.{name}({paramsText})",
            Summary = string.IsNullOrEmpty(summary) ? null : summary,
            MemberId = "rhinoscript:" + name,
            Returns = returns,
            Parameters = parameters,
        };
    }

    private static string? FirstDocLine(string? docstring)
    {
        if (string.IsNullOrEmpty(docstring))
        {
            return null;
        }

        foreach (var line in docstring.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0)
            {
                return t;
            }
        }

        return null;
    }

    private static readonly string[] SectionHeaders = { "Parameters:", "Returns:", "Example:", "See Also:", "Notes:" };
    private static readonly Regex DocParamLine = new(@"^\s+(\w+)\s*(?:\(([^)]*)\))?\s*:\s*(.*)$", RegexOptions.Compiled);

    /// <summary>Parses the <c>Parameters:</c> and <c>Returns:</c> sections of a Google-style docstring.</summary>
    private static (IReadOnlyList<ReflectedParameter> Params, string? Returns) ParseDocSections(string? docstring)
    {
        var parameters = new List<ReflectedParameter>();
        string? returns = null;
        if (string.IsNullOrEmpty(docstring))
        {
            return (parameters, null);
        }

        var lines = docstring.Split('\n');
        var section = "";
        var returnsBuf = new StringBuilder();
        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            var header = SectionHeaders.FirstOrDefault(h => string.Equals(trimmed, h, StringComparison.Ordinal));
            if (header is not null)
            {
                section = header;
                continue;
            }

            if (section == "Parameters:")
            {
                var m = DocParamLine.Match(raw);
                if (m.Success)
                {
                    parameters.Add(new ReflectedParameter
                    {
                        Name = m.Groups[1].Value,
                        Type = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "",
                        Description = m.Groups[3].Value.Trim() is { Length: > 0 } d ? d : null,
                    });
                }
            }
            else if (section == "Returns:" && trimmed.Length > 0)
            {
                if (returnsBuf.Length > 0) returnsBuf.Append(' ');
                returnsBuf.Append(trimmed);
            }
        }

        if (returnsBuf.Length > 0)
        {
            returns = DiscoveryReflector.Truncate(returnsBuf.ToString());
        }

        return (parameters, returns);
    }

    /// <summary>Fallback parameters from the def signature when the docstring has no Parameters: section:
    /// the parameter names only, no types.</summary>
    private static IReadOnlyList<ReflectedParameter> ParamsFromSignature(string paramsText)
    {
        var result = new List<ReflectedParameter>();
        if (string.IsNullOrWhiteSpace(paramsText))
        {
            return result;
        }

        foreach (var part in SplitTopLevel(paramsText))
        {
            var name = part.Trim();
            // strip a default (name=value) and an annotation (name: type)
            var eq = name.IndexOf('=');
            if (eq >= 0) name = name.Substring(0, eq);
            var colon = name.IndexOf(':');
            if (colon >= 0) name = name.Substring(0, colon);
            name = name.Trim().TrimStart('*');
            if (name.Length == 0 || name == "self")
            {
                continue;
            }

            result.Add(new ReflectedParameter { Name = name, Type = "", Description = null });
        }

        return result;
    }

    /// <summary>Splits a param list on top-level commas (ignoring commas inside brackets/parens).</summary>
    private static IEnumerable<string> SplitTopLevel(string s)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return s.Substring(start, i - start);
                start = i + 1;
            }
        }

        if (start < s.Length)
        {
            yield return s.Substring(start);
        }
    }
}
