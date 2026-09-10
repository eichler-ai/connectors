namespace Rhino.MCPBridge.Core.Execution;

/// <summary>The name a run's undo entry gets (rhino/docs/PRD.md §07): `MCP: &lt;label&gt;` from the
/// caller, sanitised to one line and capped, else the default.</summary>
internal static class UndoLabel
{
    public const string Default = "MCP Bridge Script";
    public const int MaxLength = 60;

    public static string For(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return Default;
        }

        var oneLine = string.Join(" ", label.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (oneLine.Length > MaxLength)
        {
            oneLine = oneLine.Substring(0, MaxLength - 1) + "…";
        }

        return "MCP: " + oneLine;
    }
}
