namespace MCPBridge.Core.Discovery;

/// <summary>
/// One reflected member (or, in list_functions' unscoped case, one type), joined against its XML-doc entry
/// if one was found. This is the DTO both list_functions and search_functions build their "member" wire
/// objects from (PRD §08) -- <see cref="MCPBridge.Core.Protocol.DiscoveryResultMessage"/> maps this
/// straight onto the wire shape's field names.
/// </summary>
public sealed class MemberSignature
{
    /// <summary>The XML doc-id (<see cref="XmlDocId.GetDocId"/>'s output) -- doubles as the stable identifier describe_function's member_id disambiguation refers to.</summary>
    public required string MemberId { get; init; }

    /// <summary>"Type" / "Method" / "Property" / "Field" / "Constructor" / "Event".</summary>
    public required string Kind { get; init; }

    public required string Namespace { get; init; }

    /// <summary>Fully-qualified declaring type name.</summary>
    public required string DeclaringType { get; init; }

    public required string Name { get; init; }

    /// <summary>Compact, human-readable C#-ish rendering -- short alias/unqualified type names (see <see cref="SignatureFormatter"/>).</summary>
    public required string Signature { get; init; }

    /// <summary>Short XML-doc summary text, trimmed/whitespace-normalized and truncated for list/search results; null if no doc entry was found.</summary>
    public string? Summary { get; init; }
}
