namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// A rendered image of the Grasshopper canvas for capture_view's "canvas" target (PRD §11): the encoded
/// bytes and their pixel size. Server-facing; the adapter renders it (the canvas is WinForms-typed, reached
/// by reflection there), Core only forwards it. The bytes are already in the requested MIME type.
/// </summary>
internal sealed class GrasshopperCanvasImage
{
    public byte[] Bytes { get; }
    public int Width { get; }
    public int Height { get; }

    public GrasshopperCanvasImage(byte[] bytes, int width, int height)
    {
        Bytes = bytes;
        Width = width;
        Height = height;
    }
}

/// <summary>
/// The outcome of frame_canvas (PRD §11): what region of the canvas the viewport was set to, and how the
/// neighbourhood was resolved. Server-facing; the adapter computes it (GH-typed), Core forwards it.
/// <see cref="Rect"/> is [x, y, width, height] in canvas coordinates.
/// </summary>
internal sealed class FrameCanvasResult
{
    public string GrasshopperDocumentId { get; }
    public string Title { get; }
    /// <summary>The canvas-coordinate rectangle the viewport now frames (already padded).</summary>
    public double[] Rect { get; }
    /// <summary>How many objects fell in the framed neighbourhood.</summary>
    public int FramedObjectCount { get; }
    /// <summary>Requested components that matched an object on the canvas.</summary>
    public string[] MatchedComponents { get; }
    /// <summary>Requested components that matched nothing (surfaced, not failed).</summary>
    public string[] MissingComponents { get; }
    /// <summary>True when no components were given, so the whole definition was framed.</summary>
    public bool FramedWholeDefinition { get; }

    public FrameCanvasResult(string grasshopperDocumentId, string title, double[] rect, int framedObjectCount,
        string[] matchedComponents, string[] missingComponents, bool framedWholeDefinition)
    {
        GrasshopperDocumentId = grasshopperDocumentId;
        Title = title;
        Rect = rect;
        FramedObjectCount = framedObjectCount;
        MatchedComponents = matchedComponents;
        MissingComponents = missingComponents;
        FramedWholeDefinition = framedWholeDefinition;
    }
}
