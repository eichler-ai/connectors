namespace MCPBridge.Discovery.Tests.Fixtures;

/// <summary>
/// Exists to produce an EXACT score tie between a method and a property on one type, so the search
/// tie-break itself is the only thing that can order them.
///
/// <para>The query used against this type ("tie") matches only the TYPE name, never either member, so both
/// members get identical recall and identical precision -- <c>IdentifierRelevance</c> cannot separate them
/// even in principle. The property is named to sort BEFORE the method alphabetically ("Ability" &lt;
/// "Action"), so a test asserting the method ranks first cannot pass by alphabetical accident.</para>
/// </summary>
public class Tie
{
    /// <summary>An action you can invoke.</summary>
    public void Action()
    {
    }

    /// <summary>An ability you can read.</summary>
    public int Ability => 0;
}
