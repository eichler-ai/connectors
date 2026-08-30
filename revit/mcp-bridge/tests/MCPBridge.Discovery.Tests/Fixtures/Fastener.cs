namespace MCPBridge.Discovery.Tests.Fixtures;

/// <summary>Base fixture type declaring a member that <see cref="Bolt"/> inherits without overriding.</summary>
public class Fastener
{
    /// <summary>Tightens the fastener.</summary>
    /// <param name="turns">How many turns to apply.</param>
    public void Tighten(int turns)
    {
    }
}

/// <summary>
/// Derived fixture type that adds nothing of its own, so <c>Bolt.Tighten</c> resolves only through
/// DiscoveryCache's inherited-member union. This pair exists specifically to pin describe_function's
/// deliberate absence of a member/member_id cross-check (issue #64): a caller may legitimately pass
/// member "…Fixtures.Bolt.Tighten" (the type it queried) alongside member_id "M:…Fixtures.Fastener.Tighten"
/// (the type that DECLARES it), and those disagreeing prefixes must keep resolving.
/// </summary>
public class Bolt : Fastener
{
}
