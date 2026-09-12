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
