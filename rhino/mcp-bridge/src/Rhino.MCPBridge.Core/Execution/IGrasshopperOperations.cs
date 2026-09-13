using System;
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
    /// <summary>Wires a source object's output port to a target object's input port. An empty port name
    /// selects the sole port (or the object itself, for a free-floating parameter). Marks the target dirty
    /// but does not solve.</summary>
    void Connect(object grasshopperDocument, string sourceId, string sourceOutput, string targetId, string targetInput);
    /// <summary>Removes the wire from a source's output to a target's input (a no-op when not wired).</summary>
    void Disconnect(object grasshopperDocument, string sourceId, string sourceOutput, string targetId, string targetInput);
    /// <summary>Removes every wire feeding a target's input port.</summary>
    void ClearSources(object grasshopperDocument, string targetId, string targetInput);
    /// <summary>Saves the definition to <paramref name="path"/>, or to its current file when path is empty
    /// (raising when it has none). Writes the filesystem — gated by confirm_lifecycle_actions upstream.</summary>
    void Save(object grasshopperDocument, string path);
    void Solve(object grasshopperDocument, bool expireAll);

    /// <summary>Begins a run-scoped Grasshopper undo record: every mutation until the returned scope is
    /// disposed is grouped into ONE entry (labelled <paramref name="label"/>) the user can revert in the
    /// Grasshopper editor. A no-op scope when <paramref name="grasshopperDocument"/> is null.</summary>
    IDisposable BeginUndoRecording(object? grasshopperDocument, string label);
    /// <summary>Reads an object's current output, flattened and budget-capped (the read half of Set).</summary>
    GrasshopperValue Get(object grasshopperDocument, string nickname);
    /// <summary>Serialises a parameter's full volatile data tree, budget-capped.</summary>
    GrasshopperData Data(object grasshopperDocument, string nickname);
}
