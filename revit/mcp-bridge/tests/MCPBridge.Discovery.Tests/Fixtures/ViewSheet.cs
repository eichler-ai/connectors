namespace MCPBridge.Discovery.Tests.Fixtures;

/// <summary>
/// Mirrors the exact shape from issue #65 -- a type whose name supplies two query tokens, carrying both a
/// short, general factory method and a longer, more specialised sibling whose extra name material happens
/// to contain a third query token as a PREFIX ("place" inside "Placeholder").
///
/// <para>Named after the real Autodesk.Revit.DB.ViewSheet deliberately: the reported defect was that
/// <c>search_functions("create sheet place view")</c> ranked <c>CreatePlaceholder</c> first and did not
/// surface <c>Create</c> at all, and reproducing it needs the same name geometry, not merely a pair of
/// methods with a shared prefix. Keeping the real names makes the fixture legible against the issue.</para>
/// </summary>
public class ViewSheet
{
    /// <summary>Creates a new ViewSheet.</summary>
    public static ViewSheet Create(int titleBlockTypeId) => new();

    /// <summary>Creates a placeholder sheet in a document.</summary>
    public static ViewSheet CreatePlaceholder() => new();
}
