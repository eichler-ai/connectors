namespace MCPBridge.Discovery.Tests.Fixtures;

/// <summary>
/// Isolates a double-counting defect an independent review of issue #75 found live against the real
/// corpus: "create" and "new" are the same <see cref="MCPBridge.Core.Discovery.IdentifierRelevance.Synonyms"/>
/// class, but query tokenization used to keep them as two SEPARATE token slots -- so this type's single
/// "Create" word-part satisfied both slots at once, buying a free unmatched-token-allowance seat no query
/// actually earned. Deliberately shares nothing with a real target object other than the synonym-class
/// word, so ANY tier-2 admission for a query naming an unrelated third token is the defect.
/// </summary>
public class Arc
{
    /// <summary>Creates a new Arc.</summary>
    public static Arc Create(double radius) => new();
}
