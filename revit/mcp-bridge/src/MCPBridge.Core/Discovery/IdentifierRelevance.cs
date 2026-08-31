using System;
using System.Collections.Generic;

namespace MCPBridge.Core.Discovery;

/// <summary>
/// Relevance scoring for <see cref="DiscoveryCache"/>'s tier-2 (name-match) search tier: how well a set of
/// query tokens explains, and is explained by, a member's own name plus its declaring type's short name.
///
/// <para>Exists because tier 2 was originally BINARY -- every query token had to be a raw
/// <c>LIKE '%token%'</c> substring of the member or type name, and every row that cleared that bar scored
/// exactly 500. Two defects followed from that, both filed as issue #65:</para>
///
/// <list type="number">
/// <item>A method missing ONE query token dropped a whole tier, below the hard 500-point band tier 3 can
/// never cross -- so it lost to any method that matched all tokens, no matter how much better it was. The
/// reported case: <c>search_functions("create sheet place view")</c> ranked
/// <c>ViewSheet.CreatePlaceholder</c> first and did not surface <c>ViewSheet.Create</c> at all, because
/// "place" is a substring of "Place<b>holder</b>" and nothing in <c>ViewSheet.Create</c> supplies it. One
/// accidental mid-word substring promoted the wrong method a full tier and demoted the right one.</item>
/// <item>Within tier 2 there was no relevance signal whatsoever -- every row scored 500, so ordering fell
/// through to DiscoveryService's tie-breakers, which are alphabetical by member name. Whenever tier 2
/// returned more rows than <c>top_n</c>, page 1 was chosen alphabetically rather than by relevance.</item>
/// </list>
///
/// <para>The fix is to make tier 2 GRADED rather than binary, on two axes. Recall alone is not enough:
/// <c>CreatePlaceholder</c> matches strictly more query tokens than <c>Create</c> does and always will, so
/// any recall-only score keeps the reported bug. The decisive signal is PRECISION -- name material the
/// query never asked for. "Placeholder" is only ever partially explained by the token "place", and that
/// shortfall is what lets the plain <c>Create</c> win.</para>
/// </summary>
internal static class IdentifierRelevance
{
    /// <summary>
    /// English function words that carry no API-name signal. Dropped from a query before matching (see
    /// <c>DiscoveryCache.TokenizeQuery</c>) because under a substring predicate short words match almost
    /// everything: "create a wall on a level" ranked <c>WallFoundation.Create</c> above <c>Wall.Create</c>
    /// purely because "a" and "on" occur inside "WallFound<b>a</b>ti<b>on</b>" and nowhere in "Wall".
    ///
    /// <para>They are ALSO skipped when measuring precision below, and that symmetry is the point.
    /// Independent PR review finding: filtering only the query side charges a candidate for name material
    /// the user did in fact type. Plenty of real Revit identifiers contain these as word-parts --
    /// <c>SynchronizeWith<b>Central</b></c>, <c>Built<b>In</b>Parameter</c>, <c>AsDouble</c>,
    /// <c>CopyToClipboard</c>, <c>CreateFromCurveLoops</c>. Measured on the real corpus, "synchronize model
    /// with central" cost <c>Document.SynchronizeWithCentral</c> 29 points for the dropped "with" alone --
    /// enough to tie it with <c>Document.IsCentralModel</c>, a bool property, which then won rank 1 on the
    /// alphabetical tie-break.</para>
    /// </summary>
    public static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "any", "are", "as", "at", "be", "by", "can", "do", "for", "from", "how", "i",
        "if", "in", "into", "is", "it", "its", "me", "my", "need", "of", "on", "onto", "or", "please",
        "that", "the", "their", "them", "then", "there", "this", "to", "want", "was", "what", "when",
        "which", "will", "with", "would",
    };

    /// <summary>
    /// Credit for a query token that equals a whole word-part of the name ("create" vs "Create").
    /// </summary>
    private const double ExactCredit = 1.0;

    /// <summary>
    /// Credit for a query token that is a prefix of a word-part ("place" vs "Placeholder", "widg" vs
    /// "Widget"). Deliberately well below <see cref="ExactCredit"/> rather than near it: prefix typing is a
    /// real and supported way to query ("trans" for Transaction), so it must score above zero, but treating
    /// it as near-equal to an exact hit is precisely what let "place" carry "Placeholder" past "Create".
    /// </summary>
    private const double PrefixCredit = 0.35;

    /// <summary>
    /// Credit for a query token appearing MID-word, matching neither the start of a word-part nor the whole
    /// of one ("older" inside "Placeholder"). The weakest evidence there is -- and the exact shape that the
    /// raw <c>LIKE '%token%'</c> predicate used to treat as a full match.
    /// </summary>
    private const double SubstringCredit = 0.15;

    /// <summary>
    /// Multiplier applied to any credit earned against the DECLARING TYPE's name rather than the member's
    /// own. A query token explained by the member name is stronger evidence than one explained by the type,
    /// since every member of a type shares the latter and it therefore separates nothing within that type.
    /// </summary>
    private const double TypeNameWeight = 0.9;

    /// <summary>
    /// Small, hand-maintained equivalence classes for Revit's own vocabulary mismatches (issue #75) --
    /// deliberately NOT stemming or an embedding: the corpus is one vendor's API with a consistent house
    /// style, and the failure this fixes is a specific vocabulary mismatch, not general morphology.
    ///
    /// <para>The defining case: Revit's factory convention is <c>NewXxx</c>, not <c>CreateXxx</c>
    /// (<c>Document.NewFamilyInstance</c>, <c>NewLevel</c>, ...), so <c>search_functions("create family
    /// instance")</c> ranked it at #16 -- behind several <c>Xxx.Create</c> overloads with nothing to do
    /// with family instances -- because no amount of retuning the tier-2 weights makes "create" and "new"
    /// the same word. The rest mirror the same shape for Revit's other common verb pairs.</para>
    ///
    /// <para>Each entry lists only the OTHER members of its class, not itself; go through
    /// <see cref="Expand"/> rather than indexing this directly.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Synonyms = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["create"] = new[] { "new" },
        ["new"] = new[] { "create" },
        ["delete"] = new[] { "remove", "erase" },
        ["remove"] = new[] { "delete", "erase" },
        ["erase"] = new[] { "delete", "remove" },
        ["get"] = new[] { "find", "lookup" },
        ["find"] = new[] { "get", "lookup" },
        ["lookup"] = new[] { "get", "find" },
        ["modify"] = new[] { "set", "change" },
        ["set"] = new[] { "modify", "change" },
        ["change"] = new[] { "modify", "set" },
    };

    /// <summary>
    /// Every word that earns the same credit as <paramref name="token"/> when matched against a name's
    /// word-parts: itself, plus its <see cref="Synonyms"/> if it has any.
    /// </summary>
    public static IReadOnlyList<string> Expand(string token)
    {
        if (!Synonyms.TryGetValue(token, out var synonyms))
        {
            return new[] { token };
        }

        var expanded = new string[synonyms.Length + 1];
        expanded[0] = token;
        Array.Copy(synonyms, 0, expanded, 1, synonyms.Length);
        return expanded;
    }

    /// <summary>
    /// Stable identifier for the synonym class <paramref name="token"/> belongs to: the lexicographically
    /// smallest member of <see cref="Expand"/>'s result. A word with no synonym class is its own key.
    ///
    /// <para>Independent-review finding on the first cut of #75: <c>DiscoveryCache.TokenizeQuery</c> kept
    /// "create" and "new" as two SEPARATE query-token slots even though they are the same class, and each
    /// slot's <see cref="Expand"/> included the other -- so a single name word-part like "Create" satisfied
    /// both slots at once. That silently bought the row a free <c>UnmatchedTokenAllowance</c> seat no query
    /// actually earned: measured live, "create a new transaction" admitted <c>Arc.Create</c> (matching only
    /// "create"/"new", nothing else) into tier 2 while <c>Transaction.Transaction</c> fell out of the top
    /// 12 entirely. This key exists so <c>TokenizeQuery</c> can de-duplicate query tokens BY CLASS, not by
    /// literal spelling, collapsing "create ... new" into one slot before admission/scoring ever run.</para>
    /// </summary>
    public static string SynonymClassKey(string token)
    {
        var key = token;
        foreach (var member in Expand(token))
        {
            if (string.CompareOrdinal(member, key) < 0)
            {
                key = member;
            }
        }

        return key;
    }

    /// <summary>
    /// Multiplier applied to credit earned ONLY through a <see cref="Synonyms"/> expansion, never to a
    /// literal exact/prefix/substring match. Independent-review finding: without this, a purely
    /// synonym-derived hit was worth exactly as much as the user's own literal word, which could rank a
    /// method the query never named above the one it did -- measured live, <c>"lookup parameter"</c> put
    /// <c>ParameterAccess.GetParameter</c> (matching only via "lookup"-&gt;"get") ahead of
    /// <c>Element.LookupParameter</c> (matching "lookup" head-on). Below 1.0 so the literal spelling always
    /// wins a fair fight; well above 0 so the #75 fix -- "create" reaching Revit's own "New" convention --
    /// still works, since that case has no literal competitor for the row it needs to promote.
    /// </summary>
    private const double SynonymCreditWeight = 0.75;

    /// <summary>
    /// Splits a CLR identifier into lowercase word-parts on camelCase/PascalCase boundaries, underscores,
    /// and letter/digit transitions: "CreatePlaceholder" -&gt; ["create", "placeholder"], "ViewSheet" -&gt;
    /// ["view", "sheet"], "ElementId" -&gt; ["element", "id"]. Consecutive capitals are held together as one
    /// part up to the last one that starts a new word, so an acronym survives intact and does not shatter
    /// into single letters: "UIApplication" -&gt; ["ui", "application"], "XYZ" -&gt; ["xyz"].
    /// </summary>
    public static IReadOnlyList<string> SplitWords(string identifier)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(identifier))
        {
            return words;
        }

        var start = 0;
        for (var i = 1; i <= identifier.Length; i++)
        {
            var atEnd = i == identifier.Length;
            var boundary = atEnd;
            if (!atEnd)
            {
                var prev = identifier[i - 1];
                var cur = identifier[i];
                // "aB" (createPlaceholder), "1a"/"a1" (Level2Plan), and the tail of a run of capitals
                // followed by a lowercase letter ("UIApp" breaks between I and A, not U and I).
                boundary = (char.IsLower(prev) && char.IsUpper(cur))
                    || (char.IsDigit(prev) != char.IsDigit(cur))
                    || (char.IsUpper(prev) && char.IsUpper(cur) && i + 1 < identifier.Length && char.IsLower(identifier[i + 1]));
            }

            if (!atEnd && (identifier[i] == '_' || identifier[i] == '`'))
            {
                boundary = true;
            }

            if (boundary)
            {
                var word = identifier[start..i].Trim('_', '`');
                if (word.Length > 0)
                {
                    words.Add(word.ToLowerInvariant());
                }

                start = i;
                // Skip the separator itself so it never begins the next word.
                while (start < identifier.Length && (identifier[start] == '_' || identifier[start] == '`'))
                {
                    start++;
                    i = start;
                }
            }
        }

        return words;
    }

    /// <summary>
    /// Best credit <paramref name="token"/> can earn against a single word-part: the literal
    /// exact/prefix/substring credit at full weight, or -- if that finds nothing, or finds less than a
    /// synonym would -- credit through one of its <see cref="Expand"/>ed synonyms, discounted by
    /// <see cref="SynonymCreditWeight"/> (independent-review finding; see that constant's own comment for
    /// why an undiscounted synonym hit is a real defect, not just a conservative choice).
    ///
    /// <para>Deliberately the ONE place synonym expansion happens, shared by both the recall loop (query
    /// token vs. name word) and the precision loop (name word vs. query token) in <see cref="Score"/>
    /// below. That symmetry is load-bearing, not incidental -- expanding only on the query side would
    /// repeat issue #65's stopword trap one review round found: "New" in <c>NewFamilyInstance</c> must
    /// count as material the query "create family instance" DID explain, or the fix gives with one hand
    /// (recall: "create" now reaches "New") and takes with the other (precision: "New" still counts as
    /// unexplained). Routing both loops through this one function is what keeps them in lockstep -- the
    /// discount below applies equally to both, for the same reason.</para>
    /// </summary>
    private static double Credit(string token, string word, bool wordIsLeading)
    {
        var best = CreditDirect(token, word);

        // Synonym credit is awarded ONLY against the LEADING word-part of a name (issue #86). Revit's
        // factory convention puts the verb first -- Document.NewFamilyInstance, NewLevel, NewGroup -- so a
        // leading "New" really is the creation verb the query "create ..." is reaching for. A "New"
        // anywhere else is almost always an adjective inside a sentence-shaped identifier:
        // NoElementsAddedtoNewAssembly, CannotCreateNewDesignOption, IsValidHostForNewRailing,
        // SaveAsNewCentral.
        //
        // Counted over the reflected corpus: 217 members carry "new" as a word-part; 176 have it leading,
        // 41 do not, and not one of the 41 is a factory.
        //
        // SCOPE, stated because the evidence is narrower than the rule: that count is about "new" alone,
        // and the factory-convention argument is specific to the create/new group. The gate nonetheless
        // applies to every group -- delete/remove/erase, get/find/lookup, modify/set/change. Revit's
        // verb-first house style makes that plausible rather than merely convenient, and the ranking
        // corpus showed no regression across 79 queries, but four of the queries that reordered
        // ("delete an element", "find the level of an element", "set a view template", "change the view
        // scale") come from the other groups and carry no expectation. Snapshot-only, so a future
        // regression there would show as a diff rather than a failure. Those 41 are worse than merely wrong -- their
        // names are long, so they match MORE query tokens than the real factory does
        // (NoElementsAddedtoNewAssembly supplies "elements", "assembly" and, via the synonym, "create",
        // where AssemblyInstance.Create supplies only two of the three), and recall carried them past the
        // method that actually does the job.
        //
        // Position is checked on the NAME side, which both of Score's loops pass in this argument -- the
        // symmetry that doc comment describes is preserved, because the rule applies identically to
        // recall and precision.
        if (wordIsLeading && Synonyms.TryGetValue(token, out var synonyms))
        {
            foreach (var synonym in synonyms)
            {
                best = Math.Max(best, SynonymCreditWeight * CreditDirect(synonym, word));
            }
        }

        return best;
    }

    /// <summary>Exact/prefix/substring credit for the literal <paramref name="token"/> against a single
    /// word-part, with no synonym expansion -- see <see cref="Credit"/> for that.</summary>
    private static double CreditDirect(string token, string word)
    {
        if (word.Equals(token, StringComparison.Ordinal))
        {
            return ExactCredit;
        }

        if (word.StartsWith(token, StringComparison.Ordinal))
        {
            return PrefixCredit;
        }

        return word.Contains(token, StringComparison.Ordinal) ? SubstringCredit : 0.0;
    }

    /// <summary>
    /// Relevance of one member to <paramref name="queryTokens"/>, in (0, 1] for any row that matched at all.
    ///
    /// <para>The product of two fractions:</para>
    /// <list type="bullet">
    /// <item><b>Recall</b> -- how much of the QUERY the name accounts for. Averaged over query tokens, so an
    /// unmatched token costs a proportional share rather than disqualifying the row outright. This is what
    /// lets a strong match survive one stray natural-language word, the tier-drop half of issue #65.</item>
    /// <item><b>Precision</b> -- how much of the NAME the query accounts for. Averaged over the member's and
    /// type's word-parts, so name material the caller never asked for costs. This is the half that actually
    /// separates <c>Create</c> from <c>CreatePlaceholder</c>, and it generalizes the "prefer the shorter,
    /// more general overload" tie-break issue #65 proposed: a name is preferred when the query explains ALL
    /// of it, without that preference being hardcoded to any particular pair of members.</item>
    /// </list>
    ///
    /// <para>Multiplied rather than averaged so that a collapse on either axis is decisive -- a row that
    /// matches every token but carries a pile of unasked-for name material is not a good hit, and neither is
    /// a perfectly-explained name that answers a quarter of the query.</para>
    /// </summary>
    public static double Score(IReadOnlyList<string> queryTokens, string memberName, string typeShortName)
    {
        if (queryTokens.Count == 0)
        {
            return 0.0;
        }

        var memberWords = SplitWords(memberName);
        var typeWords = SplitWords(typeShortName);
        if (memberWords.Count + typeWords.Count == 0)
        {
            return 0.0;
        }

        // Recall: for each query token, its best credit anywhere in the name.
        var recallTotal = 0.0;
        foreach (var token in queryTokens)
        {
            var best = 0.0;
            for (var i = 0; i < memberWords.Count; i++)
            {
                best = Math.Max(best, Credit(token, memberWords[i], wordIsLeading: i == 0));
            }

            for (var i = 0; i < typeWords.Count; i++)
            {
                best = Math.Max(best, Credit(token, typeWords[i], wordIsLeading: i == 0) * TypeNameWeight);
            }

            recallTotal += best;
        }

        // Precision: for each word-part of the name, its best credit from any query token. The type's parts
        // are weighted down the same way they are for recall, so a long declaring-type name is not punished
        // as hard as unasked-for material in the member's OWN name.
        //
        // Stopword word-parts are skipped entirely -- not scored, and not counted in the denominator. The
        // query side already dropped them, so charging a name for containing one penalizes a candidate for
        // material the caller actually typed. See StopWords for the measured case.
        var precisionTotal = 0.0;
        var precisionCount = 0;
        for (var i = 0; i < memberWords.Count; i++)
        {
            var word = memberWords[i];
            if (StopWords.Contains(word))
            {
                continue;
            }

            var best = 0.0;
            foreach (var token in queryTokens)
            {
                best = Math.Max(best, Credit(token, word, wordIsLeading: i == 0));
            }

            precisionTotal += best;
            precisionCount++;
        }

        for (var i = 0; i < typeWords.Count; i++)
        {
            var word = typeWords[i];
            if (StopWords.Contains(word))
            {
                continue;
            }

            var best = 0.0;
            foreach (var token in queryTokens)
            {
                best = Math.Max(best, Credit(token, word, wordIsLeading: i == 0));
            }

            precisionTotal += TypeNameWeight * best + (1.0 - TypeNameWeight);
            precisionCount++;
        }

        // A name made up ENTIRELY of stopword parts leaves nothing to measure precision against; treat it
        // as fully explained rather than dividing by zero, so recall alone decides.
        var recall = recallTotal / queryTokens.Count;
        var precision = precisionCount == 0 ? 1.0 : precisionTotal / precisionCount;
        return recall * precision;
    }
}
