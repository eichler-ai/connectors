using Rhino.MCPBridge.Core.Protocol;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>The open Rhino documents and Grasshopper definitions as `register` needs them (PRD §05, §10),
/// taken together in one main-thread hop. Grasshopper definitions are instance-level (process-global in
/// Grasshopper's model), so they ride beside the documents rather than under one. Faked in tier 1.</summary>
internal interface IDocumentSnapshotSource
{
    DocumentSnapshot Snapshot();
}

/// <summary>The two document lists a `register` carries. <see cref="GrasshopperDocuments"/> is empty when
/// Grasshopper is not loaded into the process.</summary>
internal readonly record struct DocumentSnapshot(
    IReadOnlyList<RegisteredDocument> Documents,
    IReadOnlyList<GrasshopperDocument> GrasshopperDocuments);
