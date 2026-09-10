namespace Rhino.MCPBridge.Core.Capture;

/// <summary>
/// The adapter seam for viewport capture (PRD §11). All calls on the main thread. The service owns
/// the policy (bounds, option validation, restore-after-capture); the adapter owns the pixels.
/// </summary>
internal interface IViewCapture
{
    /// <summary>MODEL viewport names of the document, the active one first when it is a model view
    /// (a layout/page view is never listed: it is not capturable through the viewport path -- review of #283).</summary>
    IReadOnlyList<string> ViewportNames(object document);

    /// <summary>The viewport's current pixel size (for sizing the capture to its aspect).</summary>
    (int Width, int Height) ViewportSize(object document, string viewport);

    /// <summary>Known display mode names, for validating the option before touching anything.</summary>
    IReadOnlyList<string> DisplayModeNames();

    /// <summary>Captures one viewport to PNG bytes at the given size, with the given display mode (null:
    /// the viewport's own), after applying zoom (none/extents/selected). The adapter restores the
    /// viewport's projection and display mode afterwards; a failure to restore is returned as the
    /// second value, never thrown, so the image still reaches the agent (§01).</summary>
    (byte[] Bytes, string? RestoreFailure) Capture(object document, string viewport, int width, int height, string? displayMode, string zoom, bool transparent, bool grid, bool axes, string mimeType);
}
