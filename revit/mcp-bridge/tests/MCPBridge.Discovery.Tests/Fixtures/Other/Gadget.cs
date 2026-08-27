namespace MCPBridge.Discovery.Tests.Fixtures.Other;

/// <summary>
/// Deliberately the same bare type name (and same member name, Run) as <see cref="MCPBridge.Discovery.Tests.Fixtures.Gadget"/>,
/// in a different namespace -- simulates two add-ins each vendoring a same-named helper type, for testing
/// that a fully-qualified search_functions query resolves to the SPECIFIC one named, not an ambiguous tie
/// across both.
/// </summary>
public class Gadget
{
    /// <summary>Runs this OTHER gadget -- must never be the one a query for Fixtures.Gadget.Run resolves to.</summary>
    public void Run()
    {
    }
}
