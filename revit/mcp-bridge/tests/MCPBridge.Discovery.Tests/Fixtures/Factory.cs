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

/// <summary>
/// The factory half of the leading-word-part case, shaped like the real one it stands in for. The type
/// name carries the query's noun PLUS an extra word, exactly as Autodesk.Revit.DB.AssemblyInstance does,
/// which is what keeps its score low enough for a false factory to overtake it.
/// </summary>
public static class GizmoInstance
{
    /// <summary>Creates a gizmo instance.</summary>
    public static int Create() => 0;
}

/// <summary>
/// A failure-message property carrying "new" as a NON-leading word-part -- the portable stand-in for
/// BuiltInFailures.AssemblyFailures.NoElementsAddedtoNewAssembly, which took rank 1 of "create an assembly
/// from elements" away from AssemblyInstance.Create when every word-part was canonicalized rather than
/// just the leading one.
///
/// <para>The shape matters: it must match MORE query tokens than the real factory does, so that
/// canonicalizing its "new" is enough to overtake a member that genuinely creates the thing. Comparing it
/// against a factory on a same-named type would not discriminate -- that factory scores so high the false
/// one cannot catch it either way, which is how the first version of this fixture let the mutation live.
/// </para>
/// </summary>
public static class GizmoFailures
{
    /// <summary>Reported when no parts were added to a gizmo.</summary>
    public static int NoPartsAddedtoNewGizmo() => 0;
}
