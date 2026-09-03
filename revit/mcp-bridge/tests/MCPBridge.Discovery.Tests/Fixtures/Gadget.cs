namespace MCPBridge.Discovery.Tests.Fixtures;

/// <summary>A second fixture type in the same namespace as <see cref="Widget"/>, for namespace-scoped list_functions tests (multiple types flattened into one member list).</summary>
public class Gadget
{
    /// <summary>Runs the gadget.</summary>
    public void Run()
    {
    }

    /// <summary>An indexer, to exercise describe_function's parameter handling on a non-method member kind.</summary>
    /// <param name="index">The slot index.</param>
    // Renamed from the default "Item" so NamedIndexedPropertyTests can pin that the default-member check
    // compares against DefaultMemberAttribute's VALUE, not the literal name "Item" (issue #186).
    [System.Runtime.CompilerServices.IndexerName("Slot")]
    public int this[int index]
    {
        get => index;
        set { }
    }
}
