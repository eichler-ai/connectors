namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// A read-only snapshot of an open Grasshopper definition's structure for inspect_definition (PRD §10):
/// the objects on the canvas, their canvas positions, and their component-level wiring. Server-facing (it
/// rides the wire to the MCP server), NOT script-facing — a script reads data with Connector.Grasshopper.
/// The adapter builds it (it alone names Grasshopper types); Core only forwards it.
/// </summary>
internal sealed class GrasshopperDefinitionInfo
{
    /// <summary>The gh_document_id of the definition this describes, so a caller that omitted it (asking for
    /// the active canvas) learns the id to pass to execute_script / capture_view / a framed capture.</summary>
    public string GrasshopperDocumentId { get; }
    public string Title { get; }
    /// <summary>The .gh/.ghx file path when saved; null for an untitled definition.</summary>
    public string? Path { get; }
    /// <summary>Total objects in the definition (before any name filter).</summary>
    public int ObjectCount { get; }
    /// <summary>Whether the definition is enabled; a disabled definition computes no volatile data.</summary>
    public bool Enabled { get; }
    /// <summary>The objects the inspection enumerated that matched the name filter; with no filter, every
    /// object it could enumerate (normally equal to <see cref="ObjectCount"/>).</summary>
    public int MatchCount { get; }
    /// <summary>The offset into the match list this page starts at.</summary>
    public int Offset { get; }
    /// <summary>True when more matches exist past this page.</summary>
    public bool Truncated { get; }
    public IReadOnlyList<GrasshopperObjectInfo> Objects { get; }

    public GrasshopperDefinitionInfo(string grasshopperDocumentId, string title, string? path, int objectCount,
        bool enabled, int matchCount, int offset, bool truncated, IReadOnlyList<GrasshopperObjectInfo> objects)
    {
        GrasshopperDocumentId = grasshopperDocumentId;
        Title = title;
        Path = path;
        ObjectCount = objectCount;
        Enabled = enabled;
        MatchCount = matchCount;
        Offset = offset;
        Truncated = truncated;
        Objects = objects;
    }
}

/// <summary>
/// One object on a Grasshopper canvas: its identity, canvas geometry (for framing a capture), and the
/// component-level wiring resolved to neighbouring objects' instance guids. <see cref="Pivot"/> is
/// [x, y]; <see cref="Bounds"/> is [x, y, width, height] in canvas coordinates.
/// </summary>
internal sealed class GrasshopperObjectInfo
{
    public string Guid { get; }
    public string Nickname { get; }
    /// <summary>The component/parameter type name, e.g. "Number Slider", "Circle".</summary>
    public string Name { get; }
    /// <summary>"component", "param", or "other".</summary>
    public string Kind { get; }
    public double[] Pivot { get; }
    public double[] Bounds { get; }
    /// <summary>Instance guids of the objects whose outputs feed this one (its sources).</summary>
    public string[] Upstream { get; }
    /// <summary>Instance guids of the objects this one's outputs feed (its recipients).</summary>
    public string[] Downstream { get; }

    public GrasshopperObjectInfo(string guid, string nickname, string name, string kind,
        double[] pivot, double[] bounds, string[] upstream, string[] downstream)
    {
        Guid = guid;
        Nickname = nickname;
        Name = name;
        Kind = kind;
        Pivot = pivot;
        Bounds = bounds;
        Upstream = upstream;
        Downstream = downstream;
    }
}
