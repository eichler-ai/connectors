using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MCPBridge.Core.Execution;

/// <summary>
/// The `mutations` field of a SUCCESSFUL execute_script result (#146 Phase 2): what the run changed, net,
/// across every committed transaction -- so an agent that just wrote does not need a read-after-write
/// script to learn whether "12 walls" actually landed. Present only when something changed; a read-only
/// run carries no field at all, and a failed run never does (its changes were rolled back).
///
/// Counts are NET across the run: an element created and then deleted in the same run contributes
/// nothing; an element created and then modified counts once, as created. <see cref="Deleted"/> is a
/// count only -- a deleted element has no category left to ask. <see cref="Modified"/> is noisy by
/// nature (Revit marks dependents modified on regeneration), which the field's own documentation says.
/// </summary>
public sealed class MutationReport
{
    public MutationReport(int created, int modified, int deleted, IReadOnlyDictionary<string, CategoryTally> byCategory, bool truncated)
    {
        Created = created;
        Modified = modified;
        Deleted = deleted;
        ByCategory = byCategory;
        Truncated = truncated;
    }

    [JsonPropertyName("created")]
    public int Created { get; }

    [JsonPropertyName("modified")]
    public int Modified { get; }

    [JsonPropertyName("deleted")]
    public int Deleted { get; }

    /// <summary>Per-category created/modified tallies, keyed by Revit category name; elements with no category land under "(none)".</summary>
    [JsonPropertyName("by_category")]
    public IReadOnlyDictionary<string, CategoryTally> ByCategory { get; }

    /// <summary>True when category resolution hit its cap during the run, so <see cref="ByCategory"/> undercounts; the totals are still exact.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; }

    /// <summary>Convenience for callers; never on the wire (the field is omitted entirely when empty).</summary>
    [JsonIgnore]
    public bool IsEmpty => Created == 0 && Modified == 0 && Deleted == 0;
}

public sealed class CategoryTally
{
    public CategoryTally(int created, int modified)
    {
        Created = created;
        Modified = modified;
    }

    [JsonPropertyName("created")]
    public int Created { get; }

    [JsonPropertyName("modified")]
    public int Modified { get; }
}
