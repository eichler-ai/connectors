namespace MCPBridge.Core.Protocol;

/// <summary>
/// One entry in a `register` message's document list (PRD §05). Document identity
/// hashing (doc-/tmp- prefixes, §09) is phase 03 scope -- here the id is passed in
/// already computed by the caller.
/// </summary>
public sealed class RegisteredDocument
{
    public string DocumentId { get; }
    public string Title { get; }
    public string? Path { get; }
    public bool IsWorkshared { get; }
    public bool IsActive { get; }

    public RegisteredDocument(string documentId, string title, string? path, bool isWorkshared, bool isActive)
    {
        DocumentId = documentId;
        Title = title;
        Path = path;
        IsWorkshared = isWorkshared;
        IsActive = isActive;
    }
}
