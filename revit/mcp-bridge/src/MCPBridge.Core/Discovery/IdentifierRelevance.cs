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

    /// <summary>Best credit <paramref name="token"/> can earn against a single word-part.</summary>
    private static double Credit(string token, string word)
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
            foreach (var word in memberWords)
            {
                best = Math.Max(best, Credit(token, word));
            }

            foreach (var word in typeWords)
            {
                best = Math.Max(best, Credit(token, word) * TypeNameWeight);
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
        foreach (var word in memberWords)
        {
            if (StopWords.Contains(word))
            {
                continue;
            }

            var best = 0.0;
            foreach (var token in queryTokens)
            {
                best = Math.Max(best, Credit(token, word));
            }

            precisionTotal += best;
            precisionCount++;
        }

        foreach (var word in typeWords)
        {
            if (StopWords.Contains(word))
            {
                continue;
            }

            var best = 0.0;
            foreach (var token in queryTokens)
            {
                best = Math.Max(best, Credit(token, word));
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
