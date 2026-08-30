using System.Linq;
using MCPBridge.Core.Discovery;
using Xunit;

namespace MCPBridge.Discovery.Tests;

/// <summary>
/// Direct unit tests for search_functions' tier-2 scorer (issue #65). Deliberately mostly ORDERING tests
/// rather than assertions on exact scores: the weights inside <see cref="IdentifierRelevance"/> are
/// empirical, and pinning literal values would make every future retune look like a regression while
/// proving nothing about the behaviour anyone actually depends on. What must hold is which member wins.
/// </summary>
public class IdentifierRelevanceTests
{
    [Theory]
    [InlineData("Create", new[] { "create" })]
    [InlineData("CreatePlaceholder", new[] { "create", "placeholder" })]
    [InlineData("ViewSheet", new[] { "view", "sheet" })]
    [InlineData("ElementId", new[] { "element", "id" })]
    [InlineData("UIApplication", new[] { "ui", "application" })]
    [InlineData("XYZ", new[] { "xyz" })]
    [InlineData("Level2Plan", new[] { "level", "2", "plan" })]
    [InlineData("get_Parameter", new[] { "get", "parameter" })]
    [InlineData("", new string[0])]
    public void SplitWords_BreaksIdentifiersOnWordBoundaries(string identifier, string[] expected)
    {
        Assert.Equal(expected, IdentifierRelevance.SplitWords(identifier).ToArray());
    }

    [Fact]
    public void Score_PrefersTheMethodWhoseNameTheQueryFullyExplains()
    {
        // The core of issue #65. Note CreatePlaceholder matches strictly MORE query tokens than Create
        // does -- "place" is a prefix of "Placeholder" and Create supplies it not at all -- so recall alone
        // ranks them the wrong way round no matter how it is weighted. The precision term (name material
        // the query never asked for) is what has to carry this, which is why it exists.
        var tokens = new[] { "create", "sheet", "place", "view" };

        var create = IdentifierRelevance.Score(tokens, "Create", "ViewSheet");
        var placeholder = IdentifierRelevance.Score(tokens, "CreatePlaceholder", "ViewSheet");

        Assert.True(create > placeholder, $"Create ({create}) must outrank CreatePlaceholder ({placeholder})");
    }

    [Fact]
    public void Score_ExactWordMatchBeatsAPrefixOfALongerWord()
    {
        var tokens = new[] { "place" };

        var exact = IdentifierRelevance.Score(tokens, "Place", "ViewSheet");
        var prefix = IdentifierRelevance.Score(tokens, "Placeholder", "ViewSheet");

        Assert.True(exact > prefix, $"exact ({exact}) must outrank prefix ({prefix})");
    }

    [Fact]
    public void Score_PrefixMatchBeatsAMidWordSubstring()
    {
        // Prefix typing ("trans" for Transaction) is a supported way to query and must stay above zero;
        // a token landing mid-word is the weakest evidence there is, and was exactly what the old raw
        // LIKE '%token%' predicate scored as a full match.
        var tokens = new[] { "place" };

        var prefix = IdentifierRelevance.Score(tokens, "Placeholder", "Sheet");
        var midWord = IdentifierRelevance.Score(tokens, "Misplaced", "Sheet");

        Assert.True(prefix > midWord, $"prefix ({prefix}) must outrank mid-word substring ({midWord})");
        Assert.True(midWord > 0, "a mid-word substring is still a match, just a weak one");
    }

    [Fact]
    public void Score_MatchOnTheMemberNameBeatsTheSameMatchOnTheTypeName()
    {
        // Every member of a type shares its type name, so a token explained only by the type separates
        // nothing within that type and is weaker evidence.
        var tokens = new[] { "wall" };

        var onMember = IdentifierRelevance.Score(tokens, "Wall", "Factory");
        var onType = IdentifierRelevance.Score(tokens, "Factory", "Wall");

        Assert.True(onMember > onType, $"member-name match ({onMember}) must outrank type-name match ({onType})");
    }

    [Fact]
    public void Score_UnmatchedQueryTokenCostsButDoesNotZero()
    {
        // The tier-drop half of issue #65, at the scorer level: a stray natural-language word must reduce
        // the score, not annihilate it -- annihilation is what the old all-or-nothing tier membership did.
        var full = IdentifierRelevance.Score(new[] { "create", "sheet" }, "Create", "ViewSheet");
        var withStray = IdentifierRelevance.Score(new[] { "create", "sheet", "zzz" }, "Create", "ViewSheet");

        Assert.True(withStray > 0, "one unmatched token must not zero the score");
        Assert.True(withStray < full, $"an unmatched token must cost something ({withStray} vs {full})");
    }

    [Fact]
    public void Score_ShorterMoreGeneralOverloadWinsWhenTheQueryExplainsBothEqually()
    {
        // Generalizes the tie-break issue #65 proposed ("prefer the shorter or more general name") without
        // hardcoding it: the shorter name simply has less unexplained material to be penalized for.
        var tokens = new[] { "wall", "create" };

        var shorter = IdentifierRelevance.Score(tokens, "Create", "Wall");
        var longer = IdentifierRelevance.Score(tokens, "CreateFromCurveLoops", "Wall");

        Assert.True(shorter > longer, $"Wall.Create ({shorter}) must outrank Wall.CreateFromCurveLoops ({longer})");
    }

    [Fact]
    public void Score_StopWordPartsInTheNameAreFree()
    {
        // 2nd review round. Stopwords are dropped from the QUERY, so charging a candidate for containing
        // one in its NAME penalizes it for material the caller actually typed. Plenty of real Revit
        // identifiers carry them as word-parts: SynchronizeWithCentral, BuiltInParameter, AsDouble,
        // CopyToClipboard, CreateFromCurveLoops. Measured on the real corpus, the dropped "with" cost
        // Document.SynchronizeWithCentral 29 points -- enough to tie it with Document.IsCentralModel, a
        // bool property, which then took rank 1 on the alphabetical tie-break.
        var tokens = new[] { "synchronize", "central" };

        var withStopWord = IdentifierRelevance.Score(tokens, "SynchronizeWithCentral", "Document");
        var withoutStopWord = IdentifierRelevance.Score(tokens, "SynchronizeCentral", "Document");

        Assert.Equal(withoutStopWord, withStopWord, precision: 9);
    }

    [Fact]
    public void Score_NameOfOnlyStopWordParts_DoesNotDivideByZero()
    {
        // Skipping stopwords in the precision loop empties the denominator for a name made entirely of
        // them. Guarded to 1.0 (fully explained) so recall alone decides, rather than producing NaN and
        // poisoning every comparison it takes part in.
        var score = IdentifierRelevance.Score(new[] { "the" }, "The", "It");

        Assert.False(double.IsNaN(score), "score must not be NaN");
        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void Score_IsBoundedToTheUnitInterval()
    {
        // DiscoveryCache multiplies this by its tier-2 band width and adds it to the tier floor, so a score
        // above 1.0 would let tier 2 collide with tier 1's flat 1000.
        var cases = new[]
        {
            (new[] { "create" }, "Create", "ViewSheet"),
            (new[] { "view", "sheet", "create" }, "Create", "ViewSheet"),
            (new[] { "a" }, "A", "A"),
        };

        foreach (var (tokens, member, type) in cases)
        {
            var score = IdentifierRelevance.Score(tokens, member, type);
            Assert.InRange(score, 0.0, 1.0);
        }
    }
}
