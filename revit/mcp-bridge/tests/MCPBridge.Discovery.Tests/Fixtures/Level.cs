namespace MCPBridge.Discovery.Tests.Fixtures;

/// <summary>
/// Isolates the SQL-admission half of issue #75, distinct from <see cref="Document"/>/
/// <see cref="ImportInstance"/>, which isolate the ranking half. Named after the real
/// <c>Autodesk.Revit.Creation.Document.NewLevel</c>: a SHORT two-token query gives the unmatched-token
/// allowance zero slack (<c>UnmatchedTokenAllowance</c>), so a query relying entirely on the synonym word
/// ("create" reaching "New") must be admitted by the SQL predicate itself, not merely score well once
/// admitted -- IdentifierRelevance.Score never runs on a row DiscoveryCache.QueryTokenMatch never returns.
/// </summary>
public class Level
{
    /// <summary>Creates a new Level.</summary>
    public static Level NewLevel(double elevation) => new();
}
