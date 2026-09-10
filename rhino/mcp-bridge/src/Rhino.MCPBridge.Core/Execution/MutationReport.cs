using System.Text.Json.Serialization;

namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// What a successful run changed, NET (rhino/docs/PRD.md §07, after Revit's #146 Phase 2): an
/// object added then deleted contributes nothing; added then replaced counts once as added.
/// Present only on a success that changed something. Built by <see cref="MutationTracker"/> from
/// RhinoDoc's add/delete/replace/undelete events for the run's duration.
/// </summary>
public sealed class MutationReport
{
    [JsonPropertyName("net_added")] public int NetAdded { get; init; }
    [JsonPropertyName("net_modified")] public int NetModified { get; init; }
    [JsonPropertyName("net_deleted")] public int NetDeleted { get; init; }
    /// <summary>Per object type name (Rhino's ObjectType: Curve, Brep, Mesh, …), same three counts.</summary>
    [JsonPropertyName("by_object_type")] public IReadOnlyDictionary<string, MutationTally> ByObjectType { get; init; } = new Dictionary<string, MutationTally>();
    /// <summary>Per layer full path, same three counts.</summary>
    [JsonPropertyName("by_layer")] public IReadOnlyDictionary<string, MutationTally> ByLayer { get; init; } = new Dictionary<string, MutationTally>();

    [JsonIgnore] public bool IsEmpty => NetAdded == 0 && NetModified == 0 && NetDeleted == 0;
}

public sealed class MutationTally
{
    [JsonPropertyName("added")] public int Added { get; init; }
    [JsonPropertyName("modified")] public int Modified { get; init; }
    [JsonPropertyName("deleted")] public int Deleted { get; init; }
}
