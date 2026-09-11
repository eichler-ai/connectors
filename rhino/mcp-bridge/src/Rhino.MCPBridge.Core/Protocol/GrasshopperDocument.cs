namespace Rhino.MCPBridge.Core.Protocol;

/// <summary>
/// One open Grasshopper definition as <c>register</c> advertises it (PRD §10). Reported at the INSTANCE
/// level (a sibling of <see cref="RegisteredDocument"/>, not nested under one), because Grasshopper's
/// document server is process-global: a definition is not owned by a particular <c>RhinoDoc</c>. An agent
/// addresses one with <c>gh_document_id</c> to bind a script's <c>ghdoc</c>/<c>GrasshopperDocument</c>
/// global to it (a later PR) and to scope the solve report to it.
/// </summary>
public sealed class GrasshopperDocument
{
    /// <summary><c>gh-&lt;hash&gt;</c> from the definition's path, or a session-stable unsaved id (PRD §12).</summary>
    public string GrasshopperDocumentId { get; }
    public string Title { get; }
    /// <summary>Absolute path of a saved <c>.gh</c>/<c>.ghx</c>; null when unsaved.</summary>
    public string? Path { get; }
    /// <summary>Whether this is Grasshopper's active document.</summary>
    public bool IsActive { get; }
    /// <summary>Whether the definition is enabled (a disabled definition does not solve).</summary>
    public bool IsEnabled { get; }
    /// <summary>Number of objects (components + params) on the canvas — a cheap size signal.</summary>
    public int ComponentCount { get; }

    public GrasshopperDocument(string grasshopperDocumentId, string title, string? path, bool isActive, bool isEnabled, int componentCount)
    {
        GrasshopperDocumentId = grasshopperDocumentId;
        Title = title;
        Path = path;
        IsActive = isActive;
        IsEnabled = isEnabled;
        ComponentCount = componentCount;
    }
}
