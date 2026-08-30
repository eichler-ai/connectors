namespace MCPBridge.Discovery.Tests.Fixtures;

/// <summary>
/// Carries the low-relevance member-name matches. See <see cref="Flange"/> for what this fixture is for.
///
/// <para>Declared FIRST on purpose. Types are reflected, and rows inserted, in metadata order, so this is
/// what puts the rows the scorer ranks LAST at the front of the unordered candidate scan. Without it the
/// cap test passes even when the cap is applied in scan order -- verified by mutation, which is how the
/// original arrangement (Flange first) was caught passing for the wrong reason.</para>
/// </summary>
public static class Bracket
{
    /// <summary>A long name that happens to contain the queried word.</summary>
    public static void FlangeAlignmentToleranceOverride() { }

    /// <summary>A long name that happens to contain the queried word.</summary>
    public static void FlangeSeatingDepthCalibration() { }

    /// <summary>A long name that happens to contain the queried word.</summary>
    public static void FlangeRetentionClipInspection() { }
}

/// <summary>
/// Fixture for issue #76: the tier-2 candidate cap has to be applied AFTER scoring, not by any SQL-side
/// proxy for the score.
///
/// <para>The shape that separates the two is a type-name-only match that outscores a member-name match.
/// Every member here matches the single token "flange", but in opposite ways: <see cref="Flange"/>'s
/// members match on their declaring TYPE name and contribute almost no unexplained name material, while
/// <see cref="Bracket"/>'s match on their own member name and drown that one matched word in four
/// unmatched ones. Relevance ranks Flange's members first; the old SQL ordering
/// (<c>member_hits DESC</c>) ranks Bracket's first, because a type-name match scores no member hits at
/// all. So a cap smaller than the candidate count returns disjoint sets under the two rules.</para>
///
/// <para>Static so no implicit constructor joins the candidate set -- a constructor is scored on its type
/// name alone, which would put it top of the ranking and blur what this fixture is isolating.</para>
/// </summary>
public static class Flange
{
    /// <summary>Reads the flange.</summary>
    public static int Read() => 0;

    /// <summary>Writes the flange.</summary>
    public static int Write() => 0;
}
