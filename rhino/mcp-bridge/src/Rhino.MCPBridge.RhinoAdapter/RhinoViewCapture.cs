using System.Drawing.Imaging;
using Rhino.Display;
using Rhino.DocObjects.Tables;
using Rhino.MCPBridge.Core.Capture;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// The pixels half of capture_view (PRD §11) over Rhino.Display.ViewCapture. Main thread only. The
/// viewport's projection and display mode are pushed before and restored after a capture that
/// changes them, so a capture is invisible to the person except for a redraw; a restore failure is
/// reported, never thrown.
/// </summary>
internal sealed class RhinoViewCapture : IViewCapture
{
    public IReadOnlyList<string> ViewportNames(object document)
    {
        var doc = (RhinoDoc)document;
        var model = doc.Views.GetViewList(ViewTypeFilter.Model).Select(v => v.ActiveViewport.Name).Distinct().ToList();
        var active = doc.Views.ActiveView;
        // Active first -- only when the active view is a model view. A layout/page view is not in the
        // model list and must not be offered as "active": Find() could never resolve it (review of #283).
        if (active is not null && active is not RhinoPageView && model.Remove(active.ActiveViewport.Name))
        {
            model.Insert(0, active.ActiveViewport.Name);
        }

        return model;
    }

    public (int Width, int Height) ViewportSize(object document, string viewport)
    {
        var view = Find((RhinoDoc)document, viewport);
        var s = view?.ActiveViewport.Size ?? new System.Drawing.Size(1280, 720);
        return (s.Width, s.Height);
    }

    public IReadOnlyList<string> DisplayModeNames() =>
        DisplayModeDescription.GetDisplayModes().Select(d => d.EnglishName).ToList();

    public (byte[] Bytes, string? RestoreFailure) Capture(object document, string viewport, int width, int height, string? displayMode, string zoom, bool transparent, bool grid, bool axes, string mimeType)
    {
        var doc = (RhinoDoc)document;
        var view = Find(doc, viewport) ?? throw new InvalidOperationException($"viewport '{viewport}' disappeared");
        var vp = view.ActiveViewport;
        var pushed = false; // paired on what actually happened, not on intent (review of #283)
        string? restoreFailure = null;
        try
        {
            if (zoom != "none")
            {
                vp.PushViewProjection();
                pushed = true;
                if (zoom == "extents") vp.ZoomExtents(); else vp.ZoomExtentsSelected();
            }

            System.Drawing.Bitmap? bitmap;
            if (displayMode is not null)
            {
                // Setting RhinoViewport.DisplayMode before a ViewCapture does not take effect for the
                // shot (found live: a "Shaded" request came back wireframe), so a requested mode goes
                // through the overload that renders WITH a mode -- which takes none of the flags; the
                // service reports that as capture-options-ignored.
                var mode = DisplayModeDescription.FindByName(displayMode)
                    ?? throw new InvalidOperationException($"display mode '{displayMode}' was not found at capture time");
                bitmap = view.CaptureToBitmap(new System.Drawing.Size(width, height), mode);
            }
            else
            {
                var capture = new ViewCapture
                {
                    Width = width,
                    Height = height,
                    ScaleScreenItems = false,
                    DrawAxes = axes,
                    DrawGrid = grid,
                    DrawGridAxes = grid,
                    TransparentBackground = transparent,
                };
                bitmap = capture.CaptureToBitmap(view);
            }

            using var image = bitmap ?? throw new InvalidOperationException("Rhino returned no image (the viewport may be minimised or the display mode unavailable)");
            using var ms = new MemoryStream();
            // Rhino's System.Drawing.Common on the Mac has no ImageCodecInfo/EncoderParameters
            // (TypeLoadException at runtime, verified live), so JPEG goes through the plain
            // Save(Stream, ImageFormat) overload at the encoder's default quality.
            image.Save(ms, mimeType == "image/png" ? ImageFormat.Png : ImageFormat.Jpeg);

            return (ms.ToArray(), restoreFailure);
        }
        finally
        {
            if (pushed)
            {
                try { vp.PopViewProjection(); }
                catch (Exception ex) { restoreFailure = "PopViewProjection: " + ex.Message; }
            }

            try { view.Redraw(); } catch { /* cosmetic: the next redraw repaints */ }
        }
    }

    private static RhinoView? Find(RhinoDoc doc, string viewport)
    {
        foreach (var v in doc.Views.GetViewList(ViewTypeFilter.Model))
        {
            if (string.Equals(v.ActiveViewport.Name, viewport, StringComparison.OrdinalIgnoreCase)) return v;
        }

        return null;
    }
}
