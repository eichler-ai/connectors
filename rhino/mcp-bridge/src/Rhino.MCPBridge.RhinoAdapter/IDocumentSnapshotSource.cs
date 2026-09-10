using Rhino.MCPBridge.Core.Protocol;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>The open documents as `register` needs them, taken on the main thread. Faked in tier 1.</summary>
internal interface IDocumentSnapshotSource
{
    IReadOnlyList<RegisteredDocument> Snapshot();
}
