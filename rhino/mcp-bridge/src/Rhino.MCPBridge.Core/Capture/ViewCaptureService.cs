using Rhino.MCPBridge.Core.Diagnostics;

namespace Rhino.MCPBridge.Core.Capture;

/// <summary>
/// The policy half of capture_view (PRD §11): validates the request, bounds the image (default
/// 1024 px on the long edge, hard cap 2048, so a capture never approaches the client's output
/// ceiling), fans `all` out to every viewport, and turns a restore failure into a notice. Tier-1
/// tested against a fake <see cref="IViewCapture"/>.
/// </summary>
internal sealed class ViewCaptureService
{
    private readonly IViewCapture _capture;

    public ViewCaptureService(IViewCapture capture) { _capture = capture; }

    public sealed class Result
    {
        public List<CapturedImage> Images { get; } = new();
        public List<DiagnosticRecord> Notices { get; } = new();
    }

    /// <summary>Throws <see cref="CaptureRequestException"/> (a §01 record) for an invalid request
    /// BEFORE any viewport is touched.</summary>
    public Result Capture(object document, CaptureRequest request)
    {
        var zoom = request.Zoom?.ToLowerInvariant() ?? "none";
        if (zoom is not ("none" or "extents" or "selected"))
        {
            throw new CaptureRequestException(Invalid("zoom", $"zoom must be one of none, extents, selected; got '{request.Zoom}'"));
        }

        if (request.Width < 0 || request.Height < 0)
        {
            throw new CaptureRequestException(Invalid("width", "width and height must be positive when given"));
        }

        var format = request.Format?.ToLowerInvariant() ?? "jpeg";
        if (format is not ("jpeg" or "jpg" or "png"))
        {
            throw new CaptureRequestException(Invalid("format", $"format must be jpeg or png; got '{request.Format}'"));
        }

        string? mode = null;
        if (!string.IsNullOrWhiteSpace(request.DisplayMode))
        {
            var known = _capture.DisplayModeNames();
            mode = known.FirstOrDefault(n => string.Equals(n, request.DisplayMode, StringComparison.OrdinalIgnoreCase));
            if (mode is null)
            {
                throw new CaptureRequestException(Invalid("display_mode", $"display mode '{request.DisplayMode}' is not one this Rhino has; known: {string.Join(", ", known)}"));
            }
        }

        var names = _capture.ViewportNames(document);
        if (names.Count == 0)
        {
            throw new CaptureRequestException(DiagnosticRecord.Create(DiagnosticSeverity.Error, "no-viewports", DiagnosticSource.Execution,
                "the document has no viewports to capture", null, new[] { "open a model with a viewport (a headless document has none)" }));
        }

        IEnumerable<string> targets = request.Target.ToLowerInvariant() switch
        {
            "active" => new[] { names[0] },
            "all" => names,
            _ => names.Where(n => string.Equals(n, request.Target, StringComparison.OrdinalIgnoreCase)).Take(1),
        };
        var list = targets.ToList();
        if (list.Count == 0)
        {
            throw new CaptureRequestException(DiagnosticRecord.Create(DiagnosticSeverity.Error, "viewport-not-found", DiagnosticSource.Execution,
                $"no viewport named '{request.Target}'; this document has: {string.Join(", ", names)}",
                new Dictionary<string, object?> { ["viewports"] = names },
                new[] { "pass one of the listed names, \"active\", or \"all\"" }));
        }

        var result = new Result();
        var mime = request.ResolvedMimeType;
        if (mode is not null && (request.TransparentBackground || !request.DrawGrid || !request.DrawAxes))
        {
            // Rhino's render-with-a-mode overload takes none of these flags (found live); saying so beats
            // silently dropping them (observability over silence -- review of #283).
            result.Notices.Add(DiagnosticRecord.Create(DiagnosticSeverity.Info, "capture-options-ignored", DiagnosticSource.Execution,
                "transparent_background, draw_grid and draw_axes do not apply when display_mode is set: Rhino renders a requested mode with the viewport's own grid/axes settings and an opaque background",
                null, new[] { "omit display_mode to have those flags honoured (the viewport's current mode is used), or accept the mode's defaults" }));
        }

        foreach (var viewport in list)
        {
            var (w, h) = Size(request, _capture.ViewportSize(document, viewport));
            var (bytes, restoreFailure) = _capture.Capture(document, viewport, w, h, mode, zoom, request.TransparentBackground, request.DrawGrid, request.DrawAxes, mime);
            result.Images.Add(new CapturedImage { Viewport = viewport, Width = w, Height = h, MimeType = mime, Bytes = bytes });
            if (restoreFailure is not null)
            {
                result.Notices.Add(DiagnosticRecord.Create(DiagnosticSeverity.Warning, "capture-restore-failed", DiagnosticSource.Execution,
                    $"viewport '{viewport}' was captured, but its previous view state could not be fully restored: {restoreFailure}",
                    new Dictionary<string, object?> { ["viewport"] = viewport }, new[] { "the person's viewport may show the capture's zoom or display mode; a manual Zoom Previous restores it" }));
            }
        }

        return result;
    }

    /// <summary>Bounded output size: an explicit width/height is capped at the max edge; otherwise the
    /// viewport's own aspect scaled so the long edge is the default. Never below the minimum edge.</summary>
    internal static (int Width, int Height) Size(CaptureRequest request, (int Width, int Height) viewport)
    {
        double vw = Math.Max(viewport.Width, 1), vh = Math.Max(viewport.Height, 1);
        double aspect = vw / vh;
        int w = request.Width, h = request.Height;
        if (w <= 0 && h <= 0)
        {
            if (aspect >= 1) { w = CaptureRequest.DefaultLongEdge; h = (int)Math.Round(w / aspect); }
            else { h = CaptureRequest.DefaultLongEdge; w = (int)Math.Round(h * aspect); }
        }
        else if (w <= 0)
        {
            w = (int)Math.Round(h * aspect);
        }
        else if (h <= 0)
        {
            h = (int)Math.Round(w / aspect);
        }

        var longEdge = Math.Max(w, h);
        if (longEdge > CaptureRequest.MaxLongEdge)
        {
            var scale = (double)CaptureRequest.MaxLongEdge / longEdge;
            w = (int)Math.Round(w * scale);
            h = (int)Math.Round(h * scale);
        }

        return (Math.Max(w, CaptureRequest.MinEdge), Math.Max(h, CaptureRequest.MinEdge));
    }

    private static DiagnosticRecord Invalid(string param, string message) => DiagnosticRecord.Create(
        DiagnosticSeverity.Error, "invalid-param", DiagnosticSource.Execution, message,
        new Dictionary<string, object?> { ["param"] = param }, new[] { "fix the parameter and call capture_view again" });
}

public sealed class CaptureRequestException : Exception
{
    public DiagnosticRecord Record { get; }
    public CaptureRequestException(DiagnosticRecord record) : base(record.Message) { Record = record; }
}
