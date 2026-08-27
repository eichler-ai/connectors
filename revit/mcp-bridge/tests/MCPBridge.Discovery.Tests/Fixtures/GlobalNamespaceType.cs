// Deliberately no `namespace` block: Type.Namespace is null for a type declared here, exercising the same
// shape a real C++/CLI interop artifact in RevitAPI.dll can produce (DiscoveryReflector.ReflectType maps a
// null Type.Namespace to "" -- see its own doc comment) -- used to test that the empty/global namespace is
// excluded from list_functions' namespaces tier rather than appearing as an unreachable dead end.

/// <summary>A type in the global namespace, for testing the empty-namespace exclusion.</summary>
public class GlobalNamespaceType
{
    /// <summary>Does nothing.</summary>
    public void DoNothing()
    {
    }
}
