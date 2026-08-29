using System.Collections.Generic;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// Thin seam over Autodesk.Revit.UI.UIApplication (PRD §06), used by RequestDispatcher to obtain the full
/// IUiDocumentAdapter it needs. The real UIApplication a script binds to as its `UIApplication` global
/// (PRD §14) comes from <see cref="IRawUiApplicationSource"/>, which the real adapter also implements.
///
/// The two document-routing members below live directly ON this interface -- unlike
/// IExistingDocumentSource's separate-capability shape -- because neither names a Revit type, so the
/// tier-1 fakes can implement them without dragging a RevitAPI reference into the test assembly (the
/// constraint that forced IExistingDocumentSource to be separate in the first place; see its doc comment).
/// </summary>
internal interface IUiApplicationAdapter
{
    /// <summary>The document active in the foreground when the script began running, if any.</summary>
    IUiDocumentAdapter? ActiveUiDocument { get; }

    /// <summary>
    /// Every open, non-linked document's (PRD §09 document_id, title, is-active) summary -- the
    /// candidates list for execute_script's document_id routing error (PRD §01: the error names what
    /// IS addressable, so the agent can correct without a list_instances round trip).
    /// </summary>
    IReadOnlyList<OpenDocumentInfo> OpenDocuments { get; }

    /// <summary>
    /// The open, non-linked document whose PRD §09 identity equals <paramref name="documentId"/>,
    /// wrapped for the executor to open a managed transaction on -- or null if no open document has
    /// that identity. This is what makes execute_script's document_id a real routing parameter
    /// instead of the accepted-but-ignored one it shipped as (v1 integrated review; CONVENTIONS.md's
    /// advertised-but-unimplemented clause exists because of it).
    /// </summary>
    IDocumentAdapter? FindOpenDocument(string documentId);
}
