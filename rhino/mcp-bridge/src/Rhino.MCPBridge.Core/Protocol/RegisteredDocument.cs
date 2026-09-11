namespace Rhino.MCPBridge.Core.Protocol;

/// <summary>One open document as `register` advertises it (PRD §05, §12). No worksharing flag:
/// Rhino has none. Grasshopper documents ride alongside (PRD §10) once phase 4 lands.</summary>
public sealed class RegisteredDocument
{
    public string DocumentId { get; }
    public string Title { get; }
    /// <summary>Absolute path of a saved document; null when unsaved.</summary>
    public string? Path { get; }
    public bool IsActive { get; }
    /// <summary>The connector's last completed run on this document, or null (PRD §05).</summary>
    public Execution.LastRun? LastRun { get; }

    public RegisteredDocument(string documentId, string title, string? path, bool isActive, Execution.LastRun? lastRun = null)
    {
        DocumentId = documentId;
        Title = title;
        Path = path;
        IsActive = isActive;
        LastRun = lastRun;
    }

    public RegisteredDocument WithLastRun(Execution.LastRun? lastRun) => new(DocumentId, Title, Path, IsActive, lastRun);
}
