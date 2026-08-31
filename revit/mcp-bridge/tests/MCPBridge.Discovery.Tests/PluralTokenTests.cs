using System;
using System.Linq;
using MCPBridge.Core.Discovery;
using Xunit;

namespace MCPBridge.Discovery.Tests;

/// <summary>
/// Issue #87: a plural in the query matched no word-part, so tier 2 never fired.
///
/// <para>Revit's API names are singular -- <c>WallUtils.AllowWallJoinAtEnd</c>, <c>Element</c>,
/// <c>Parameter</c> -- while a person asks about "walls" or "elements". Admission and scoring both work
/// on word-parts, so "walls" simply did not match "wall", and the row was never a candidate. Measured on
/// the real corpus: <c>"join walls at corner"</c> admitted 181 candidates and <b>zero</b> tier-2 rows,
/// while <c>"join wall at corner"</c> admitted 503 and 8 -- putting the three correct <c>WallUtils</c>
/// members at ranks 1-3 instead of leaving page 1 to <c>BuiltInParameter</c> noise.</para>
///
/// <para>The property worth asserting is not "this one query improved" -- the corpus snapshot covers
/// that. It is that <b>a plural and its singular now answer the same question the same way</b>. That is
/// what the fix claims, it generalises past the reported query, and it is the thing a future retune could
/// silently break while leaving the reported query green.</para>
/// </summary>
public class PluralTokenTests
{
    /// <summary>
    /// Phrasings that differ ONLY in plurality must produce the same leading results.
    ///
    /// <para>Compares the top three by member id rather than by score: scores can differ legitimately
    /// (a plural token still contributes its own literal credit where a name happens to contain it), but
    /// the ANSWERS should not depend on whether the caller typed an "s".</para>
    /// </summary>
    [Theory]
    [InlineData("join wall at corner", "join walls at corner")]
    [InlineData("intersect two solid", "intersect two solids")]
    [InlineData("find the workset in a model", "find the worksets in a model")]
    [InlineData("filter element by category", "filter elements by category")]
    public void SingularAndPluralPhrasingsAgree(string singular, string plural)
    {
        var built = RealRevitCorpus.TryBuild();
        if (built is null)
        {
            return;
        }

        using var context = built.Value.Context;
        using var cache = built.Value.Cache;
        var service = new DiscoveryService(cache);

        var fromSingular = TopMemberIds(service, singular);
        var fromPlural = TopMemberIds(service, plural);

        Assert.True(fromSingular.Count > 0, $"'{singular}' returned nothing; the fixture is broken");
        Assert.Equal(fromSingular, fromPlural);
    }

    /// <summary>
    /// The guards matter as much as the stripping. A word ending in "ss"/"us"/"is" is not a plural, and
    /// mangling it would silently widen every query containing one -- "class" -> "clas" would start
    /// matching nothing, or worse, matching by substring somewhere unintended.
    /// </summary>
    [Theory]
    [InlineData("walls", "wall")]
    [InlineData("elements", "element")]
    [InlineData("parameters", "parameter")]
    [InlineData("class", null)]
    [InlineData("status", null)]
    [InlineData("axis", null)]
    [InlineData("ids", null)]
    [InlineData("wall", null)]
    public void SingularizeStripsOnlyRealPlurals(string token, string? expected)
    {
        Assert.Equal(expected, IdentifierRelevance.Singularize(token));
    }

    private static System.Collections.Generic.List<string> TopMemberIds(DiscoveryService service, string query) =>
        service.SearchFunctions(query, namespaceFilter: null, cursor: null, topN: 3)
            .Results
            .Select(r => r.Member.MemberId)
            .ToList();
}
