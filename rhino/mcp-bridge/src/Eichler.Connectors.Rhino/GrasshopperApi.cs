namespace Eichler.Connectors.Rhino;

/// <summary>
/// Drive the bound Grasshopper definition (PRD §10), reached from a script as
/// <c>connector.Grasshopper</c> / <c>Connector.Grasshopper</c>. Every method acts on the definition the
/// run addressed with <c>gh_document_id</c> (the same one bound as <c>ghdoc</c>/<c>GrasshopperDocument</c>);
/// with none bound they raise a clear error. What a script cannot do for itself the connector adds here;
/// everything else in <c>Grasshopper.Kernel</c> is still reachable directly through <c>ghdoc</c>.
/// </summary>
public sealed class GrasshopperApi
{
    private readonly IConnectorRuntime _runtime;

    internal GrasshopperApi(IConnectorRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>Finds one object on the canvas by its nickname (case-insensitive) or its instance GUID,
    /// or null when none matches. When a nickname is shared, the first match in document order wins.</summary>
    public GrasshopperComponent? Find(string nicknameOrGuid) => _runtime.GrasshopperFind(nicknameOrGuid);

    /// <summary>Sets the value of an input object by nickname — a Number Slider (a number, clamped to the
    /// slider's range and rounded to its precision), a Boolean Toggle (a bool), a Panel (text) or a Value
    /// List (an item's name or value) — and expires it so the next solve recomputes. Raises if the object
    /// is not found, does not take a settable value, or (for a Value List) the value is not one of its items;
    /// feed other inputs with <see cref="Reference"/> (document geometry) or by driving <c>ghdoc</c> directly.</summary>
    public void Set(string nickname, object value) => _runtime.GrasshopperSet(nickname, value);

    /// <summary>Links an input geometry parameter (Curve, Brep, Surface, Mesh, Point, or the generic Geometry)
    /// to Rhino document objects BY REFERENCE — <paramref name="objectIds"/> is one object id or a collection of them
    /// (a <c>System.Guid</c> or its string form) — so the definition consumes live document geometry that
    /// updates when the object changes. Replaces any existing reference on the parameter and expires it.</summary>
    public void Reference(string nickname, object objectIds) => _runtime.GrasshopperReference(nickname, objectIds);

    /// <summary>Clears the referenced document geometry (and any persistent data) from an input parameter,
    /// expiring it.</summary>
    public void ClearReference(string nickname) => _runtime.GrasshopperReference(nickname, null);

    /// <summary>Reads a parameter's current output (PRD §10) — every output item across the data tree's
    /// branches, flattened into one list and capped to the response budget — as a <see cref="GrasshopperValue"/>.
    /// Numbers/text/booleans ride verbatim; geometry is summarised as type + bounding box + a document handle,
    /// never inline. The read half of <see cref="Set"/>: call <see cref="Solve"/> first so the value is current.
    /// Returns an empty value (<c>Count</c> 0) when the parameter computed nothing — usually because the
    /// definition is disabled or has not solved. Raises if the object is not found or is not a parameter with a
    /// data tree (address a component's output parameter by nickname). For the full tree with branch paths use
    /// <see cref="Data"/>.</summary>
    public GrasshopperValue Get(string nickname) => _runtime.GrasshopperGet(nickname);

    /// <summary>Serialises a parameter's full volatile data tree (PRD §10) as a <see cref="GrasshopperData"/>:
    /// the branches with their paths, item counts, and per-item summaries (numbers/text verbatim, geometry as
    /// type + bounding box + a document handle — never full geometry inline), bounded so a large tree stays
    /// within the response budget (<see cref="GrasshopperData.Truncated"/>/<see cref="GrasshopperData.Note"/>
    /// say when a cap was hit). Call <see cref="Solve"/> first so the data is current. Raises if the object is
    /// not found or is not a parameter with a data tree.</summary>
    public GrasshopperData Data(string nickname) => _runtime.GrasshopperData(nickname);

    /// <summary>Runs a solution on the definition (PRD §10). <paramref name="expireAll"/> forces every
    /// object to recompute; otherwise only what has been expired since the last solve does. The solve is
    /// synchronous on the main thread, so a slow one makes the run <c>running</c>; the resulting solve
    /// report rides the run's result (the <c>grasshopper</c> field).</summary>
    public void Solve(bool expireAll = false) => _runtime.GrasshopperSolve(expireAll);
}

/// <summary>One Grasshopper object as <see cref="GrasshopperApi.Find"/> reports it.</summary>
public sealed class GrasshopperComponent
{
    /// <summary>The object's instance GUID (its stable id on the canvas).</summary>
    public string Guid { get; }
    /// <summary>The object's nickname (what appears on the canvas), or its name when unset.</summary>
    public string Nickname { get; }
    /// <summary>The object's component/parameter type name (e.g. "Number Slider", "Curve").</summary>
    public string Type { get; }

    internal GrasshopperComponent(string guid, string nickname, string type)
    {
        Guid = guid;
        Nickname = nickname;
        Type = type;
    }
}
