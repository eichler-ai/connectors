using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            var doc = XDocument.Load(stream);
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
                        parameters[paramName] = Normalize(paramElement.Value);
                    }
                }

                var summaryElement = member.Element("summary");
                var returnsElement = member.Element("returns");

                members[name] = new XmlDocEntry
                {
                    Summary = summaryElement is null ? null : Normalize(summaryElement.Value),
                    Parameters = parameters,
                    Returns = returnsElement is null ? null : Normalize(returnsElement.Value),
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
}
