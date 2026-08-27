namespace MCPBridge.Discovery.Tests.Fixtures;

// Deliberately NOT doc-commented: exercises DiscoveryService's documented-types narrowing (a public type
// with no <member name="T:..."/> entry in the assembly's XML-doc sidecar is hidden from the *browsing*
// paths -- unscoped/namespace-scoped list_functions and search_functions -- but must stay reachable by an
// explicit type_name/describe_function lookup).
public class Undocumented
{
    public void UndocumentedWork()
    {
    }
}

/// <summary>
/// An internal type with a public nested type -- the case <c>IsPublic || IsNestedPublic</c> got wrong
/// (<c>IsNestedPublic</c> is true here even though nothing outside this assembly can see the nested type).
/// <see cref="System.Type.IsVisible"/> is what correctly excludes it.
/// </summary>
internal class InternalOuter
{
    /// <summary>Public, but nested inside an internal type, so not externally visible.</summary>
    public class NestedPublic
    {
        /// <summary>Never discoverable, because its declaring type isn't externally visible.</summary>
        public void NestedPublicWork()
        {
        }
    }
}
