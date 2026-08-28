namespace MCPBridge.RevitAdapter;

/// <summary>
/// Thin seam over Autodesk.Revit.DB.Document (PRD §06/§09), used by TransactionScriptExecutor to build
/// the ambient Transaction/TransactionGroup it wraps every script run in.
///
/// Phase 3 (PRD §14, "Real Revit API access from scripts"): <see cref="RawDocument"/> is the sanctioned
/// way to reach the real Autodesk.Revit.DB.Document a script now binds to as its `Document` global. It
/// replaces the unsupported reflection-into-a-private-field workaround `skill.md` used to document.
/// CreateTransaction/CreateTransactionGroup remain executor-only concerns -- they are OUR adapter methods,
/// not real Revit API, and were never reachable from a script anyway; what actually keeps real Document
/// exposure safe is ScriptApiDenylist (MCPBridge.Core), which rejects at compile time any script that
/// opens its own Autodesk.Revit.DB.Transaction/TransactionGroup/SubTransaction against the same document
/// the executor has already opened one on.
/// </summary>
public interface IDocumentAdapter
{
    /// <summary>Human-readable title, for diagnostics only -- not a stable identity.</summary>
    string Title { get; }

    ITransactionAdapter CreateTransaction(string name);

    ITransactionGroupAdapter CreateTransactionGroup(string name);

    /// <summary>Local file path if this document has been saved, else null (PRD §09 document identity).</summary>
    string? PathName { get; }

    /// <summary>Whether this document is workshared (local or cloud/ACC central) -- PRD §09.</summary>
    bool IsWorkshared { get; }

    /// <summary>
    /// The user-visible central model path when <see cref="IsWorkshared"/> is true, else null.
    /// Never the local copy's path -- PRD §09: "per-user and regenerated on every fresh local copy".
    /// </summary>
    string? CentralModelPath { get; }

    /// <summary>
    /// This document's stable `doc-&lt;hash&gt;`/`tmp-&lt;guid&gt;` identity (PRD §09). A plain string,
    /// not a Revit type -- crosses the Core/RevitAdapter seam without Core ever needing to see the
    /// underlying Autodesk.Revit.DB.Document. Independent PR review finding: this MUST be resolved
    /// once and cached per live Document, not recomputed on every access -- resolving it fresh each
    /// time would re-mint a brand-new tmp-&lt;guid&gt; on every single call for an unsaved document
    /// (see DocumentIdentity.Resolve's own doc comment), scattering that document's published files
    /// across a different workspace directory on every execute_script call. Real implementations
    /// (RevitDocumentAdapter) delegate to DocumentIdentity's shared, process-lifetime cache so
    /// every IDocumentAdapter wrapping the same live Document -- however many times it's freshly
    /// constructed -- agrees on the same id, and so this agrees with whatever `register`/list_instances
    /// already reported for the same document.
    /// </summary>
    string DocumentId { get; }
}
