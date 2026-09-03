using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MCPBridge.AddIn;

/// <summary>
/// The "MCP Bridge" panel's button images, drawn in code at startup rather than shipped as PNG
/// resources. A Revit PushButton with no LargeImage renders as a small text-only item, and three of
/// those side by side read as one run-on line ("Status Reconnect MCP Server:") -- the user's own
/// feedback on #187. With a 32px image each button becomes a normal large ribbon button with its
/// label underneath. Drawing them here keeps the add-in free of an image-asset pipeline for four
/// glyphs, and lets the mode button's colour say Local (calm) vs REMOTE (orange) at a glance.
/// </summary>
internal static class RibbonIcons
{
    private const int Size = 32;
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
    private static readonly Brush LocalFill = new SolidColorBrush(Color.FromRgb(0x4A, 0x7D, 0xB5));
    private static readonly Brush RemoteFill = new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x1F));

    /// <summary>A ring with an "i": connection status and build info.</summary>
    public static ImageSource Status() => Render(dc =>
    {
        dc.DrawEllipse(null, new Pen(Ink, 2.5), new Point(16, 16), 13, 13);
        DrawText(dc, "i", 17, Ink, new Point(16, 16));
    });

    /// <summary>A circular arrow: drop and re-dial the MCP Server.</summary>
    public static ImageSource Reconnect() => Render(dc =>
    {
        // 300 degrees of arc, clockwise from the top, ending in an arrowhead.
        var pen = new Pen(Ink, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(16, 4), isFilled: false, isClosed: false);
            ctx.ArcTo(new Point(5.6, 22), new Size(12, 12), 0, isLargeArc: true, SweepDirection.Counterclockwise, isStroked: true, isSmoothJoin: true);
        }

        dc.DrawGeometry(null, pen, geometry);
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(new Point(16, 4), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(22, 0), true, true);
            ctx.LineTo(new Point(22, 9), true, true);
        }

        dc.DrawGeometry(Ink, null, head);
    });

    /// <summary>One machine: the MCP Server on this computer.</summary>
    public static ImageSource ServerLocal() => Render(dc =>
    {
        DrawMachine(dc, new Rect(6, 5, 20, 15), LocalFill);
        dc.DrawRectangle(Ink, null, new Rect(11, 22, 10, 2.5));
        dc.DrawRectangle(Ink, null, new Rect(14, 20, 4, 3));
    });

    /// <summary>Two machines joined by a link, in a warning colour: the MCP Server is elsewhere.</summary>
    public static ImageSource ServerRemote() => Render(dc =>
    {
        DrawMachine(dc, new Rect(1, 9, 12, 9), Ink);
        DrawMachine(dc, new Rect(19, 9, 12, 9), RemoteFill);
        var pen = new Pen(RemoteFill, 2.5) { DashStyle = new DashStyle(new double[] { 1.2, 1.2 }, 0) };
        dc.DrawLine(pen, new Point(13, 13.5), new Point(19, 13.5));
        dc.DrawRectangle(Ink, null, new Rect(4, 20, 6, 2));
        dc.DrawRectangle(RemoteFill, null, new Rect(22, 20, 6, 2));
    });

    private static void DrawMachine(DrawingContext dc, Rect bounds, Brush fill)
    {
        dc.DrawRoundedRectangle(fill, null, bounds, 2, 2);
        var screen = new Rect(bounds.X + 2.5, bounds.Y + 2.5, bounds.Width - 5, bounds.Height - 5);
        dc.DrawRectangle(Brushes.White, null, screen);
    }

    private static void DrawText(DrawingContext dc, string text, double size, Brush brush, Point center)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            size,
            brush,
            pixelsPerDip: 1.0);
        dc.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    private static ImageSource Render(Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            draw(dc);
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
