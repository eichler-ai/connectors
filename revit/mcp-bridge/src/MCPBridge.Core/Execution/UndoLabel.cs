using System.Globalization;
using System.Linq;
using System.Text;

namespace MCPBridge.Core.Execution;

/// <summary>
/// The name a run's assimilated TransactionGroup carries into Revit's Undo history (#146 Phase 2b). The
/// undo entry is the human's backstop when an agent errs; a stack of identical "MCP Bridge Script" rows
/// made it clean but not legible. Three tiers, first one that applies:
///   1. an agent-supplied `label` on execute_script -> "MCP: {label}" (sanitised to one line, capped);
///   2. else a label derived from THAT DOCUMENT's net mutation report -> "MCP: 12 Walls created";
///   3. else <see cref="Default"/>, the pre-Phase-2b name.
/// Pure string logic, tier-1 tested; whether Revit shows it is the live harness's job.
///
/// SANITISATION IS A SAFETY EDGE, not tidiness: the label is agent-controlled text rendered in the one UI
/// element that exists so a person can see what the agent did. Controls, format characters (bidi
/// overrides, zero-width joiners) and Unicode line/paragraph separators are all dropped, so the entry
/// cannot render as something other than what it says; the cap never splits a surrogate pair.
/// </summary>
internal static class UndoLabel
{
    public const string Default = "MCP Bridge Script";
    public const string Prefix = "MCP: ";

    /// <summary>Longest label Revit's Undo dropdown shows without truncating into uselessness; chosen by eye, stated so it can be revisited.</summary>
    public const int MaxLength = 80;

    /// <summary>Input is cut here BEFORE sanitising, so an unbounded label costs bounded work.</summary>
    private const int MaxInputLength = 400;

    /// <summary>Tier 1: the agent's own words, made safe for a one-line menu entry. Null/blank yields null so the caller falls through.</summary>
    public static string? FromAgentLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        if (label.Length > MaxInputLength)
        {
            label = label.Substring(0, MaxInputLength);
        }

        var cleaned = new StringBuilder(label.Length);
        var pendingSpace = false;
        foreach (var rune in label.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var isSeparatorLike = category is UnicodeCategory.Control or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator;
            if (category is UnicodeCategory.Format)
            {
                continue; // bidi overrides, zero-width joiners: invisible, so they can only mislead
            }

            if (isSeparatorLike)
            {
                pendingSpace = cleaned.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                cleaned.Append(' ');
                pendingSpace = false;
            }

            cleaned.Append(rune.ToString());
        }

        var oneLine = cleaned.ToString();
        if (oneLine.Length == 0)
        {
            return null;
        }

        return Cap(oneLine.StartsWith(Prefix, System.StringComparison.Ordinal) ? oneLine : Prefix + oneLine);
    }

    /// <summary>
    /// Tier 2: the document's net effect, in the human's terms. Leads with the dominant kind of change;
    /// names the category when one category carries every created (or every modified) element, since
    /// "12 Walls created" is what a person scanning the Undo menu wants and "12 elements created" is not.
    /// Revit category names are plural, so a count of one is phrased as "1 element created (Walls)".
    /// </summary>
    public static string? FromReport(MutationReport? report)
    {
        if (report is null)
        {
            return null;
        }

        var parts = new System.Collections.Generic.List<string>();
        if (report.Created > 0)
        {
            parts.Add(Phrase(report.Created, SoleCategory(report, tally => tally.Created, report.Created), "created"));
        }

        if (report.Modified > 0)
        {
            parts.Add(Phrase(report.Modified, SoleCategory(report, tally => tally.Modified, report.Modified), "modified"));
        }

        if (report.Deleted > 0)
        {
            parts.Add(Phrase(report.Deleted, null, "deleted"));
        }

        return parts.Count == 0 ? null : Cap(Prefix + string.Join(", ", parts));
    }

    private static string Phrase(int count, string? soleCategory, string verb) =>
        soleCategory is null
            ? $"{count} {(count == 1 ? "element" : "elements")} {verb}"
            : count == 1
                ? $"1 element {verb} ({soleCategory})"
                : $"{count} {soleCategory} {verb}";

    /// <summary>The one category that accounts for the whole count, or null when the changes span categories or the elements had none.</summary>
    private static string? SoleCategory(MutationReport report, System.Func<CategoryTally, int> count, int total)
    {
        var sole = report.ByCategory.Where(kv => count(kv.Value) > 0).ToList();
        return sole.Count == 1 && count(sole[0].Value) == total && sole[0].Key != "(none)" ? sole[0].Key : null;
    }

    private static string Cap(string s)
    {
        if (s.Length <= MaxLength)
        {
            return s;
        }

        var cut = MaxLength - 1;
        if (char.IsHighSurrogate(s[cut - 1]))
        {
            cut--; // never leave a lone high surrogate at the end
        }

        return s.Substring(0, cut) + "\u2026";
    }
}
