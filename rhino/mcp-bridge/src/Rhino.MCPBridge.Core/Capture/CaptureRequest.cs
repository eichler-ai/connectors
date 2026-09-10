namespace Rhino.MCPBridge.Core.Capture;

/// <summary>What the agent asked for (rhino/docs/PRD.md §11), already validated and bounded by
/// <see cref="ViewCaptureService"/>. `Zoom` is one of none/extents/selected. Grasshopper canvas
/// and object isolation land with phase 4 and a display conduit respectively.</summary>
public sealed class CaptureRequest
{
    public const int DefaultLongEdge = 1280;
    public const int MaxLongEdge = 2048;
    public const int MinEdge = 64;

    /// <summary>"active", "all", or a viewport name (case-insensitive).</summary>
    public required string Target { get; init; }
    public string? DisplayMode { get; init; }
    /// <summary>"none" | "extents" | "selected"</summary>
    public string Zoom { get; init; } = "none";
    public int Width { get; init; }
    public int Height { get; init; }
    public bool TransparentBackground { get; init; }
    public bool DrawGrid { get; init; } = true;
    public bool DrawAxes { get; init; } = true;
}

/// <summary>One captured image, PNG-encoded.</summary>
public sealed class CapturedImage
{
    public required string Viewport { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Png { get; init; }
}
