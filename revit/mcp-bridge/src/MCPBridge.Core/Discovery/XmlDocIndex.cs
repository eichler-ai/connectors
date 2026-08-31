using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MCPBridge.Core.Discovery;

/// <summary>One parsed &lt;member&gt; node from an XML-doc sidecar file: summary/params/returns text, whitespace-normalized.</summary>
public sealed class XmlDocEntry
{
    public string? Summary { get; init; }

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = EmptyParams;

    public string? Returns { get; init; }

    private static readonly IReadOnlyDictionary<string, string> EmptyParams = new Dictionary<string, string>();
}

/// <summary>
/// Parses one standard .NET XML-doc-comment sidecar file (e.g. RevitAPI.xml, next to RevitAPI.dll) into a
/// lookup from XML doc-id string (<see cref="XmlDocId.GetDocId"/>'s output format, e.g. "M:Namespace.Type.
/// Method(ParamType)") to its parsed summary/params/returns text -- the same file Visual Studio IntelliSense
/// reads, and the same source PRD §08's discovery commands join reflected members against.
///
/// <para>
/// Degrades gracefully rather than throwing: a missing or unreadable/malformed XML file (e.g. a dev/test
/// environment with no real Revit install) yields <see cref="Empty"/> -- discovery still works, just
/// without doc-comment text, since the file is a joinable enrichment, not a hard dependency for reflection
/// itself.
/// </para>
/// </summary>
public sealed class XmlDocIndex
{
    private readonly Dictionary<string, XmlDocEntry> _members;

    private XmlDocIndex(Dictionary<string, XmlDocEntry> members)
    {
        _members = members;
    }

    public static XmlDocIndex Empty { get; } = new(new Dictionary<string, XmlDocEntry>());

    public bool TryGet(string docId, out XmlDocEntry entry)
    {
        if (_members.TryGetValue(docId, out var found))
        {
            entry = found;
            return true;
        }

        entry = null!;
        return false;
    }

    /// <summary>Loads and parses the sidecar file at <paramref name="path"/>. Never throws -- returns <see cref="Empty"/> on any failure (missing file, unreadable, malformed XML).</summary>
    public static XmlDocIndex LoadFromFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Empty;
            }

            using var stream = File.OpenRead(path);
            return LoadFromStream(stream);
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>Same as <see cref="LoadFromFile"/> but from an already-open stream (used directly by tests against checked-in fixture assets).</summary>
    public static XmlDocIndex LoadFromStream(Stream stream)
    {
        try
        {
            // PreserveWhitespace is LOAD-BEARING, and its absence was a real defect in shipped
            // agent-facing text. XDocument's default drops whitespace-only text nodes between sibling
            // elements, so two consecutive <para> blocks -- which the raw XML separates with a newline
            // and indentation, and nothing else -- rendered with no separator at all. Found by reading
            // describe_function's actual output during the issue #91 audit: "...calling this on them
            // fails.Order matters against Revit APIs...". Not specific to our own docs; Revit's XML uses
            // <para> heavily, so this degraded its summaries the same way. Normalize() below already
            // collapses the restored whitespace to a single space, so nothing else needs to change.
            var doc = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            var members = new Dictionary<string, XmlDocEntry>(StringComparer.Ordinal);

            foreach (var member in doc.Root?.Element("members")?.Elements("member") ?? Enumerable.Empty<XElement>())
            {
                var name = (string?)member.Attribute("name");
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var paramElement in member.Elements("param"))
                {
                    var paramName = (string?)paramElement.Attribute("name");
                    if (!string.IsNullOrEmpty(paramName))
                    {
                        parameters[paramName] = Normalize(RenderText(paramElement));
                    }
                }

                var summaryElement = member.Element("summary");
                var returnsElement = member.Element("returns");

                members[name] = new XmlDocEntry
                {
                    Summary = summaryElement is null ? null : Normalize(RenderText(summaryElement)),
                    Parameters = parameters,
                    Returns = returnsElement is null ? null : Normalize(RenderText(returnsElement)),
                };
            }

            return new XmlDocIndex(members);
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>Collapses interior whitespace/newlines (XML doc comments are typically hand-indented multi-line text) into single spaces, trimmed.</summary>
    private static string Normalize(string text) => WhitespaceRun.Replace(text, " ").Trim();

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Review finding (M5): XElement.Value concatenates only text nodes -- a self-closing
    /// &lt;see cref="..."/&gt; or &lt;paramref name="..."/&gt; contributes nothing, silently dropping the
    /// word from the middle of a sentence (e.g. "Deletes the &lt;paramref name="elementId"/&gt; from the
    /// document" rendered as "Deletes the  from the document"). Revit's XML docs use &lt;see cref&gt;
    /// heavily. Renders those inline elements as the short (last-segment) name from their cref/name
    /// attribute instead of dropping them.
    /// </summary>
    private static string RenderText(XElement element)
    {
        var sb = new StringBuilder();
        AppendText(element, sb);
        return sb.ToString();
    }

    private static void AppendText(XElement element, StringBuilder sb)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    sb.Append(text.Value);
                    break;
                case XElement el when el.Name.LocalName is "see" or "seealso":
                    var cref = (string?)el.Attribute("cref");
                    sb.Append(cref is not null ? ShortNameFromCref(cref) : (string?)el.Attribute("langword") ?? "");
                    break;
                case XElement el when el.Name.LocalName is "paramref" or "typeparamref":
                    sb.Append((string?)el.Attribute("name") ?? "");
                    break;
                // BLOCK-LEVEL elements get an explicit separator, and this is not redundant with
                // LoadOptions.PreserveWhitespace above -- the two fix different halves of the same defect
                // and neither subsumes the other. PreserveWhitespace restores the newline the SOURCE FILE
                // puts between two </para><para> pairs, which is what compiler-emitted XML always has. But
                // a tool-generated sidecar may emit <para>A</para><para>B</para> on one line, with no
                // whitespace to restore, and that would still render "AB". Injecting here is
                // formatting-independent. Conversely, injection alone would not separate whitespace-only
                // gaps between INLINE siblings (`<see cref="X"/>\n<see cref="Y"/>` -> "XY"), which
                // PreserveWhitespace does handle. Normalize() collapses the doubled spaces either way.
                case XElement el when el.Name.LocalName
                    is "para" or "list" or "item" or "description" or "term" or "code" or "br":
                    sb.Append(' ');
                    AppendText(el, sb);
                    sb.Append(' ');
                    break;
                case XElement el:
                    AppendText(el, sb); // unrecognized nested element -- still walk into it for its own text.
                    break;
            }
        }
    }

    /// <summary>Strips a cref's "T:"/"M:"/etc. prefix, parameter list, and namespace/declaring-type qualification down to just the member/type's own short name -- readable inline text, not a full doc-id.</summary>
    private static string ShortNameFromCref(string cref)
    {
        var colonIndex = cref.IndexOf(':');
        var afterPrefix = colonIndex >= 0 ? cref[(colonIndex + 1)..] : cref;
        var parenIndex = afterPrefix.IndexOf('(');
        var namePart = parenIndex >= 0 ? afterPrefix[..parenIndex] : afterPrefix;
        var lastDot = namePart.LastIndexOf('.');
        return lastDot >= 0 ? namePart[(lastDot + 1)..] : namePart;
    }
}
