namespace Rhino.MCPBridge.Core.Execution;

/// <summary>One document event as the adapter reports it (RhinoDoc.AddRhinoObject / DeleteRhinoObject /
/// ReplaceRhinoObject / UndeleteRhinoObject). Keyed by the object's id so the tracker can net.</summary>
public sealed class DocumentChange
{
    public enum Kind { Added, Deleted, Replaced, Undeleted }

    public Kind Change { get; }
    public Guid ObjectId { get; }
    /// <summary>Rhino's ObjectType name for the object (after the change, for a replace).</summary>
    public string ObjectType { get; }
    /// <summary>The object's layer full path, or "" when unknown.</summary>
    public string Layer { get; }

    public DocumentChange(Kind change, Guid objectId, string objectType, string layer)
    {
        Change = change;
        ObjectId = objectId;
        ObjectType = objectType;
        Layer = layer;
    }
}
