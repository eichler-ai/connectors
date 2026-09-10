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
        var names = new List<string>();
        var active = doc.Views.ActiveView;
        if (active is not null) names.Add(active.ActiveViewport.Name);
        foreach (var v in doc.Views.GetViewList(ViewTypeFilter.Model))
        {
            var n = v.ActiveViewport.Name;
            if (!names.Contains(n)) names.Add(n);
        }

        return names;
    }

    public (int Width, int Height) ViewportSize(object document, string viewport)
    {
        var view = Find((RhinoDoc)document, viewport);
        var s = view?.ActiveViewport.Size ?? new System.Drawing.Size(1280, 720);
        return (s.Width, s.Height);
    }

    public IReadOnlyList<string> DisplayModeNames() =>
        DisplayModeDescription.GetDisplayModes().Select(d => d.EnglishName).ToList();

    public (byte[] Png, string? RestoreFailure) Capture(object document, string viewport, int width, int height, string? displayMode, string zoom, bool transparent, bool grid, bool axes)
    {
        var doc = (RhinoDoc)document;
        var view = Find(doc, viewport) ?? throw new InvalidOperationException($"viewport '{viewport}' disappeared");
        var vp = view.ActiveViewport;
        var changedProjection = zoom != "none";
        string? restoreFailure = null;
        try
        {
            if (changedProjection)
            {
                vp.PushViewProjection();
                if (zoom == "extents") vp.ZoomExtents(); else vp.ZoomExtentsSelected();
            }

            System.Drawing.Bitmap? bitmap;
            if (displayMode is not null)
            {
                // Setting RhinoViewport.DisplayMode before a ViewCapture does not take effect for the
                // shot (found live: a "Shaded" request came back wireframe), so a requested mode goes
                // through the overload that renders WITH a mode, at the cost of the grid/axes/
                // transparency flags, which that overload does not take.
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
            image.Save(ms, ImageFormat.Png);
            return (ms.ToArray(), restoreFailure);
        }
        finally
        {
            try
            {
                if (changedProjection) vp.PopViewProjection();
                view.Redraw();
            }
            catch (Exception ex)
            {
                restoreFailure = ex.Message;
            }
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
