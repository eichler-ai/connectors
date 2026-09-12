using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using Rhino.MCPBridge.Core.Capture;
using Rhino.MCPBridge.Core.Execution;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// Renders the live Grasshopper canvas by reflection (PRD §11). The canvas (GH_Canvas) and its viewport are
/// WinForms-typed, which this assembly cannot name (the same reason RhinoRunHost.ActiveGrasshopperDocument
/// uses reflection), so every canvas/viewport member is reached reflectively; only System.Drawing types
/// (Bitmap, Color) are named directly. Reached only once Grasshopper is loaded, on the main thread.
/// </summary>
internal static class GrasshopperCanvas
{
    private const BindingFlags PubInstance = BindingFlags.Public | BindingFlags.Instance;

    /// <summary>The active GH_Canvas control, or null when the Grasshopper editor window is not open.</summary>
    internal static object? ActiveCanvas()
    {
        var instances = Type.GetType("Grasshopper.Instances, Grasshopper");
        return instances?.GetProperty("ActiveCanvas", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
    }

    /// <summary>Renders the active canvas to an encoded image, or null when no editor canvas is open. Throws
    /// only on a genuine render failure (the dispatcher surfaces that as capture-failed).</summary>
    internal static GrasshopperCanvasImage? Render(string mimeType, bool transparent, int requestedWidth, int requestedHeight)
    {
        var canvas = ActiveCanvas();
        if (canvas is null)
        {
            return null;
        }

        var viewport = canvas.GetType().GetProperty("Viewport", PubInstance)?.GetValue(canvas);
        if (viewport is null)
        {
            return null;
        }

        var method = ResolveRenderMethod(canvas.GetType(), viewport.GetType())
            ?? throw new InvalidOperationException("this Grasshopper build has no GH_Canvas.GenerateHiResImageTile(viewport, Color) method to render the canvas.");

        var background = transparent ? Color.Transparent : Color.White;
        Bitmap? raw;
        try
        {
            // Unwrap so a genuine render failure surfaces its real cause, not the opaque reflection wrapper.
            raw = method.Invoke(canvas, new[] { viewport, (object)background }) as Bitmap;
        }
        catch (System.Reflection.TargetInvocationException tie)
        {
            throw tie.InnerException ?? tie;
        }

        if (raw is null)
        {
            // The editor IS open (canvas + viewport resolved) but the render produced nothing: a real
            // failure, not the "canvas unavailable" case, so throw rather than return null.
            throw new InvalidOperationException("Grasshopper produced no image when rendering the canvas.");
        }

        try
        {
            // Bound the image with capture_view's own size policy (default long edge, hard cap, requested size).
            var req = new CaptureRequest
            {
                Target = "canvas",
                Width = requestedWidth,
                Height = requestedHeight,
                Format = mimeType == "image/png" ? "png" : "jpeg",
            };
            var (w, h) = ViewCaptureService.Size(req, (raw.Width, raw.Height));

            var sized = (w == raw.Width && h == raw.Height) ? raw : Resize(raw, w, h, transparent);
            try
            {
                using var ms = new MemoryStream();
                sized.Save(ms, mimeType == "image/png" ? ImageFormat.Png : ImageFormat.Jpeg);
                return new GrasshopperCanvasImage(ms.ToArray(), sized.Width, sized.Height);
            }
            finally
            {
                if (!ReferenceEquals(sized, raw))
                {
                    sized.Dispose();
                }
            }
        }
        finally
        {
            raw.Dispose();
        }
    }

    /// <summary>Finds GH_Canvas.GenerateHiResImageTile(viewport, Color) -> Bitmap. Prefers the exact
    /// viewport-typed overload; falls back to any 2-arg overload whose second parameter is a Color and which
    /// returns a Bitmap, in case a Grasshopper version declares the viewport parameter as a base type (the
    /// other GenerateHiResImage overload returns a List, so the Bitmap return type disambiguates).</summary>
    private static MethodInfo? ResolveRenderMethod(Type canvasType, Type viewportType)
    {
        var exact = canvasType.GetMethod("GenerateHiResImageTile", PubInstance, binder: null,
            types: new[] { viewportType, typeof(Color) }, modifiers: null);
        if (exact is not null)
        {
            return exact;
        }

        return Array.Find(canvasType.GetMethods(PubInstance), m =>
            m.Name == "GenerateHiResImageTile"
            && typeof(Bitmap).IsAssignableFrom(m.ReturnType)
            && m.GetParameters() is { Length: 2 } p
            && p[1].ParameterType == typeof(Color)
            && p[0].ParameterType.IsAssignableFrom(viewportType));
    }

    /// <summary>Makes <paramref name="ghDocument"/> the active canvas document (reflection). Returns false
    /// when the Document property is missing or read-only.</summary>
    internal static bool SetActiveDocument(object canvas, object ghDocument)
    {
        var prop = canvas.GetType().GetProperty("Document", PubInstance);
        if (prop is null || !prop.CanWrite)
        {
            return false;
        }

        prop.SetValue(canvas, ghDocument);
        return true;
    }

    /// <summary>Sets the canvas viewport to frame a canvas-coordinate rectangle (reflection, confirmed live):
    /// pans via <c>MidPoint</c> (the PointF document point shown at the viewport centre) and sets <c>Zoom</c>
    /// to fit the rectangle in the screen port, then recomputes the projection and repaints. Notes from the
    /// live spike: GH_Viewport declares TWO <c>Zoom</c> properties (one read/write, one write-only), so a
    /// plain GetProperty throws AmbiguousMatchException — the read/write one is chosen here; and <c>Target</c>
    /// is an integer pixel <c>Point</c>, not the document centre, so <c>MidPoint</c> is used instead.</summary>
    internal static void FrameViewport(object canvas, RectangleF rect)
    {
        var viewport = canvas.GetType().GetProperty("Viewport", PubInstance)?.GetValue(canvas);
        if (viewport is null)
        {
            return;
        }

        var vt = viewport.GetType();
        int sw = 800, sh = 600;
        if (vt.GetProperty("ScreenPort", PubInstance)?.GetValue(viewport) is Rectangle sp && sp.Width > 0 && sp.Height > 0)
        {
            sw = sp.Width;
            sh = sp.Height;
        }

        var zoom = Math.Min(sw / Math.Max(rect.Width, 1f), sh / Math.Max(rect.Height, 1f));
        // Cap zoom-in at 2x (a single small component should not fill the whole view); floor low enough that a
        // large "whole definition" region still fits (Grasshopper clamps to its own min internally too).
        zoom = Math.Clamp(zoom, 0.02f, 2.0f);
        var center = new PointF(rect.X + (rect.Width / 2f), rect.Y + (rect.Height / 2f));

        FindWritableProperty(vt, "Zoom", typeof(float))?.SetValue(viewport, zoom);
        FindWritableProperty(vt, "MidPoint", typeof(PointF))?.SetValue(viewport, center);

        vt.GetMethod("ComputeProjection", PubInstance, binder: null, types: Type.EmptyTypes, modifiers: null)?.Invoke(viewport, null);
        canvas.GetType().GetMethod("Refresh", PubInstance, binder: null, types: Type.EmptyTypes, modifiers: null)?.Invoke(canvas, null);
    }

    /// <summary>The writable property of the given name and type, preferring the read/write one when a type
    /// declares several by that name (GH_Viewport.Zoom, which a plain GetProperty cannot disambiguate).</summary>
    private static PropertyInfo? FindWritableProperty(Type type, string name, Type propertyType)
    {
        PropertyInfo? writeOnly = null;
        foreach (var p in type.GetProperties(PubInstance))
        {
            if (p.Name != name || !p.CanWrite || p.PropertyType != propertyType)
            {
                continue;
            }

            if (p.CanRead)
            {
                return p;
            }

            writeOnly ??= p;
        }

        return writeOnly;
    }

    private static Bitmap Resize(Bitmap src, int width, int height, bool transparent)
    {
        var dst = new Bitmap(width, height);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        if (!transparent)
        {
            g.Clear(Color.White);
        }

        g.DrawImage(src, 0, 0, width, height);
        return dst;
    }
}
