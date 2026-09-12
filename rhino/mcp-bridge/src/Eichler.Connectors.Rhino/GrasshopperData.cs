namespace Eichler.Connectors.Rhino;

/// <summary>
/// One item of a Grasshopper parameter's data, as the read half of <see cref="GrasshopperApi"/> reports it
/// (PRD §10). Numbers, text and booleans ride verbatim in <see cref="Value"/>; geometry never does — it is
/// summarised as its <see cref="Type"/>, its axis-aligned <see cref="Box"/>, and (when it references a Rhino
/// document object) that object's <see cref="Handle"/> — so a large tree serialises within the response
/// budget. To get the geometry itself, read it from the Rhino document by its handle, or reach the goo
/// through <c>ghdoc</c>.
/// </summary>
public sealed class GrasshopperItem
{
    /// <summary>What the item is: <c>"number"</c>, <c>"text"</c>, <c>"boolean"</c>, <c>"geometry"</c> or
    /// <c>"other"</c>.</summary>
    public string Kind { get; }
    /// <summary>The value for a number/text/boolean item (a <c>double</c>, <c>string</c> or <c>bool</c>);
    /// null for geometry (see <see cref="Box"/>/<see cref="Handle"/>) and for an item with no simple
    /// value.</summary>
    public object? Value { get; }
    /// <summary>The item's Grasshopper type name (e.g. "Number", "Curve", "Brep").</summary>
    public string Type { get; }
    /// <summary>For a geometry item, its world axis-aligned bounding box as
    /// <c>[minX, minY, minZ, maxX, maxY, maxZ]</c>; null for non-geometry or an empty/invalid box.</summary>
    public double[]? Box { get; }
    /// <summary>For a geometry item that references a Rhino document object, that object's id (a GUID string)
    /// — the stable handle to read the real geometry from the document; null otherwise.</summary>
    public string? Handle { get; }

    internal GrasshopperItem(string kind, object? value, string type, double[]? box, string? handle)
    {
        Kind = kind;
        Value = value;
        Type = type;
        Box = box;
        Handle = handle;
    }
}

/// <summary>
/// A Grasshopper object's current output, flattened, as <see cref="GrasshopperApi.Get"/> reports it
/// (PRD §10): every output item across all data-tree branches in one list, bounded to a budget. For the
/// full tree with its branch paths, use <see cref="GrasshopperApi.Data"/>.
/// </summary>
public sealed class GrasshopperValue
{
    /// <summary>The object's nickname (or name when unset).</summary>
    public string Nickname { get; }
    /// <summary>The object's component/parameter type name.</summary>
    public string Type { get; }
    /// <summary>How many output items the object holds in total (before any budget cap).</summary>
    public int Count { get; }
    /// <summary>The output items, flattened across branches and capped to the budget; when fewer than
    /// <see cref="Count"/>, <see cref="Truncated"/> is true.</summary>
    public GrasshopperItem[] Items { get; }
    /// <summary>True when <see cref="Items"/> holds fewer than <see cref="Count"/> items because the budget
    /// cap was reached.</summary>
    public bool Truncated { get; }

    internal GrasshopperValue(string nickname, string type, int count, GrasshopperItem[] items, bool truncated)
    {
        Nickname = nickname;
        Type = type;
        Count = count;
        Items = items;
        Truncated = truncated;
    }
}

/// <summary>One branch of a Grasshopper data tree: its path, how many items it holds, and those items
/// (capped to the budget).</summary>
public sealed class GrasshopperBranch
{
    /// <summary>The branch path, as Grasshopper writes it (e.g. <c>"{0;0}"</c>).</summary>
    public string Path { get; }
    /// <summary>How many items this branch holds in total (before any budget cap).</summary>
    public int Count { get; }
    /// <summary>The branch's items, capped to the budget; when fewer than <see cref="Count"/> the tree's
    /// <see cref="GrasshopperData.Truncated"/> is true.</summary>
    public GrasshopperItem[] Items { get; }

    internal GrasshopperBranch(string path, int count, GrasshopperItem[] items)
    {
        Path = path;
        Count = count;
        Items = items;
    }
}

/// <summary>
/// A Grasshopper parameter's full volatile data tree, as <see cref="GrasshopperApi.Data"/> reports it
/// (PRD §10): the branches with their paths and per-item summaries (numbers/text verbatim, geometry as
/// type + bounding box + handle), bounded so a large tree stays within the response budget. When a cap is
/// hit, <see cref="Truncated"/> is true and <see cref="Note"/> says what was dropped.
/// </summary>
public sealed class GrasshopperData
{
    /// <summary>The parameter's nickname (or name when unset).</summary>
    public string Nickname { get; }
    /// <summary>The parameter's type name.</summary>
    public string Type { get; }
    /// <summary>How many branches the tree has in total (before any budget cap).</summary>
    public int BranchCount { get; }
    /// <summary>How many items the tree holds across all branches in total (before any budget cap).</summary>
    public int ItemCount { get; }
    /// <summary>The branches, capped to the budget.</summary>
    public GrasshopperBranch[] Branches { get; }
    /// <summary>True when a branch or item cap dropped part of the tree from this summary.</summary>
    public bool Truncated { get; }
    /// <summary>When <see cref="Truncated"/>, a short note on what was capped; null otherwise.</summary>
    public string? Note { get; }

    internal GrasshopperData(string nickname, string type, int branchCount, int itemCount, GrasshopperBranch[] branches, bool truncated, string? note)
    {
        Nickname = nickname;
        Type = type;
        BranchCount = branchCount;
        ItemCount = itemCount;
        Branches = branches;
        Truncated = truncated;
        Note = note;
    }
}
