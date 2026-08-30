namespace MCPBridge.Discovery.Tests.Fixtures;

/// <summary>
/// Mirrors the exact shape from issue #75. Named after the real Autodesk.Revit.Creation.Document /
/// Autodesk.Revit.DB.ImportInstance pair deliberately, same rationale as <see cref="ViewSheet"/>: the
/// reported defect was that <c>search_functions("create family instance")</c> ranked
/// <c>Document.NewFamilyInstance</c> at rank 16, well below <c>ImportInstance.Create</c> and its siblings,
/// because Revit's own factory convention is "New", not "Create" -- no name/type-name credit tuning closes
/// that gap, only recognizing "create" and "new" as the same request does.
/// </summary>
public class Document
{
    /// <summary>Creates a new family instance in the document.</summary>
    public static Document NewFamilyInstance(int symbolId) => new();
}

/// <summary>The accidental winner: matches "create" and "instance" head-on with a short, fully-explained
/// name, even though it has nothing to do with family instances.</summary>
public class ImportInstance
{
    /// <summary>Creates a new ImportInstance.</summary>
    public static ImportInstance Create(int linkId) => new();
}
