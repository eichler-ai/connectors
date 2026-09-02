using System.Linq;

namespace MCPBridge.Core.Execution;

/// <summary>
/// The name a run's assimilated TransactionGroup carries into Revit's Undo history (#146 Phase 2b). The
/// undo entry is the human's backstop when an agent errs; a stack of identical "MCP Bridge Script" rows
/// made it clean but not legible. Three tiers, first one that applies:
///   1. an agent-supplied `label` on execute_script -> "MCP: {label}" (sanitised to one line, capped);
///   2. else a label derived from the run's net mutation report -> "MCP: 12 Walls created";
///   3. else <see cref="Default"/>, the pre-Phase-2b name.
/// Pure string logic, tier-1 tested; whether Revit shows it is the live harness's job.
/// </summary>
internal static class UndoLabel
{
    public const string Default = "MCP Bridge Script";
    public const string Prefix = "MCP: ";

    /// <summary>Longest label Revit's Undo dropdown shows without truncating into uselessness; chosen by eye, stated so it can be revisited.</summary>
    public const int MaxLength = 80;

    /// <summary>Tier 1: the agent's own words, made safe for a one-line menu entry. Null/blank yields null so the caller falls through.</summary>
    public static string? FromAgentLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var oneLine = new string(label.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        while (oneLine.Contains("  "))
        {
            oneLine = oneLine.Replace("  ", " ");
        }

        return Cap(oneLine.StartsWith(Prefix, System.StringComparison.Ordinal) ? oneLine : Prefix + oneLine);
    }

    /// <summary>
    /// Tier 2: the net effect, in the human's terms. Leads with the dominant kind of change; names the
    /// category when one category carries every created (or every modified) element, since "12 Walls
    /// created" is what a person scanning the Undo menu wants and "12 elements created" is not.
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
            parts.Add($"{report.Created} {SoleCategory(report, tally => tally.Created, report.Created) ?? Plural(report.Created, "element")} created");
        }

        if (report.Modified > 0)
        {
            parts.Add($"{report.Modified} {SoleCategory(report, tally => tally.Modified, report.Modified) ?? Plural(report.Modified, "element")} modified");
        }

        if (report.Deleted > 0)
        {
            parts.Add($"{report.Deleted} {Plural(report.Deleted, "element")} deleted");
        }

        return parts.Count == 0 ? null : Cap(Prefix + string.Join(", ", parts));
    }

    /// <summary>The one category that accounts for the whole count, or null when the changes span categories.</summary>
    private static string? SoleCategory(MutationReport report, System.Func<CategoryTally, int> count, int total)
    {
        var sole = report.ByCategory.Where(kv => count(kv.Value) > 0).ToList();
        return sole.Count == 1 && count(sole[0].Value) == total ? sole[0].Key : null;
    }

    private static string Plural(int n, string noun) => n == 1 ? noun : noun + "s";

    private static string Cap(string s) => s.Length <= MaxLength ? s : s.Substring(0, MaxLength - 1) + "\u2026";
}
