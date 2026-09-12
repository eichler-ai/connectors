using Eichler.Connectors.Rhino;

namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// The Grasshopper driving operations behind <c>Connector.Grasshopper</c> (PRD §10), implemented by the
/// adapter (which alone names Grasshopper types) and called by <see cref="ScriptGlobals"/>. The definition
/// is passed opaque as <see cref="object"/> (the resolved <c>GH_Document</c>) — Core never names Grasshopper
/// types — and is never null here: <see cref="ScriptGlobals"/> raises the "no definition bound" error
/// before calling, so an implementation always has a definition to act on. All members run on the main
/// thread (they are reached only from inside a run).
/// </summary>
internal interface IGrasshopperOperations
{
    GrasshopperComponent? Find(object grasshopperDocument, string nicknameOrGuid);
    void Set(object grasshopperDocument, string nickname, object value);
    /// <summary><paramref name="objectIds"/> null clears the reference; otherwise it is a Guid, its string
    /// form, or a collection of either.</summary>
    void Reference(object grasshopperDocument, string nickname, object? objectIds);
    void Solve(object grasshopperDocument, bool expireAll);
    /// <summary>Reads an object's current output, flattened and budget-capped (the read half of Set).</summary>
    GrasshopperValue Get(object grasshopperDocument, string nickname);
    /// <summary>Serialises a parameter's full volatile data tree, budget-capped.</summary>
    GrasshopperData Data(object grasshopperDocument, string nickname);
}
