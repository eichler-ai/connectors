namespace Rhino.MCPBridge.Core.Capture;

/// <summary>What the agent asked for (rhino/docs/PRD.md §11), already validated and bounded by
/// <see cref="ViewCaptureService"/>. `Zoom` is one of none/extents/selected. Grasshopper canvas
/// and object isolation land with phase 4 and a display conduit respectively.</summary>
public sealed class CaptureRequest
{
    /// <summary>Bounds, revised after the first live captures (review of #283): a 1024 px shaded PNG
    /// was ~540 KB (~720 KB base64), past what the client's output ceiling comfortably carries.
    /// Default 1024 on the long edge, JPEG unless transparency is requested.</summary>
    public const int DefaultLongEdge = 1024;
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
    /// <summary>"jpeg" (default) or "png"; a transparent background forces png.</summary>
    public string Format { get; init; } = "jpeg";

    /// <summary>The MIME type the request resolves to.</summary>
    public string ResolvedMimeType => TransparentBackground || string.Equals(Format, "png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
}

/// <summary>One captured image, encoded as <see cref="MimeType"/>.</summary>
public sealed class CapturedImage
{
    public required string Viewport { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string MimeType { get; init; }
    public required byte[] Bytes { get; init; }
}
