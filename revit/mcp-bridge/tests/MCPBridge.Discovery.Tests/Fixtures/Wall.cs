namespace MCPBridge.Discovery.Tests.Fixtures;

/// <summary>
/// Reproduces the stopword case portably. Named after the real Autodesk.Revit.DB pair for the same reason
/// as <see cref="ViewSheet"/>: the shape that matters is a short type name whose longer sibling happens to
/// contain English function words as substrings -- "WallFound<b>a</b>ti<b>on</b>" carries both "a" and
/// "on", and "Wall" carries neither.
/// </summary>
public class Wall
{
    /// <summary>Creates a new Wall.</summary>
    public static Wall Create(int levelId) => new();
}

/// <summary>A wall foundation. The longer sibling that stray function words accidentally match.</summary>
public class WallFoundation
{
    /// <summary>Creates a new WallFoundation.</summary>
    public static WallFoundation Create(int wallId) => new();
}
