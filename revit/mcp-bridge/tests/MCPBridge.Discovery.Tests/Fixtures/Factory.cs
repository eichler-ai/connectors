namespace MCPBridge.Discovery.Tests.Fixtures;

/// <summary>
/// Fixture for issue #75, reproducing Revit's <c>NewXxx</c> factory convention portably. The real case is
/// <c>Autodesk.Revit.Creation.Document.NewFamilyInstance</c>, which carries "create" in neither the member
/// name nor the declaring type and so was unreachable from the natural phrasing "create family instance".
///
/// <para>The two members are the same request under the two naming conventions, which is what lets a test
/// assert they score IDENTICALLY rather than merely that one improved.</para>
/// </summary>
public static class Factory
{
    /// <summary>Makes a gizmo, named the way Revit's own factories are named.</summary>
    public static int NewGizmo() => 0;

    /// <summary>Makes a doohickey, named the way the rest of the API is named.</summary>
    public static int CreateDoohickey() => 0;

    /// <summary>
    /// Contains the letters of "new" but is not the word. Present so the whole-word rule has something to
    /// fail against: the SQL predicate expands "create" to "new" and matches substrings, so this row IS
    /// admitted as a candidate, and only the scorer stops it ranking as a factory method.
    /// </summary>
    public static int RenewalGizmo() => 0;
}
