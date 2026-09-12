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
