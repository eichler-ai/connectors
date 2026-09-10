using Rhino.MCPBridge.Core.Capture;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Capture;

/// <summary>PRD §11's policy: bounds, option validation before any viewport is touched, fan-out, restore reporting.</summary>
public sealed class ViewCaptureServiceTests
{
    private sealed class FakeCapture : IViewCapture
    {
        public List<string> Names { get; set; } = new() { "Perspective", "Top", "Front" };
        public (int, int) Size { get; set; } = (1600, 900);
        public List<string> Modes { get; set; } = new() { "Wireframe", "Shaded", "Rendered" };
        public string? RestoreFailure { get; set; }
        public List<(string Viewport, int W, int H, string? Mode, string Zoom)> Calls { get; } = new();
        public IReadOnlyList<string> ViewportNames(object document) => Names;
        public (int Width, int Height) ViewportSize(object document, string viewport) => Size;
        public IReadOnlyList<string> DisplayModeNames() => Modes;
        public (byte[] Png, string? RestoreFailure) Capture(object document, string viewport, int width, int height, string? displayMode, string zoom, bool transparent, bool grid, bool axes)
        {
            Calls.Add((viewport, width, height, displayMode, zoom));
            return (new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, RestoreFailure);
        }
    }

    private static readonly object Doc = new();

    [Theory]
    [InlineData(0, 0, 1600, 900, 1280, 720)]   // default long edge, aspect kept
    [InlineData(0, 0, 900, 1600, 720, 1280)]   // portrait
    [InlineData(4000, 0, 1600, 900, 2048, 1152)] // explicit width capped at the max edge
    [InlineData(0, 300, 1600, 900, 533, 300)]  // height given, width from aspect
    [InlineData(10, 10, 100, 100, 64, 64)]     // never below the minimum edge
    public void Size_IsBoundedAndKeepsAspect(int reqW, int reqH, int vpW, int vpH, int wantW, int wantH)
    {
        var (w, h) = ViewCaptureService.Size(new CaptureRequest { Target = "active", Width = reqW, Height = reqH }, (vpW, vpH));
        Assert.Equal((wantW, wantH), (w, h));
    }

    [Fact]
    public void Active_CapturesTheFirstViewportOnly()
    {
        var fake = new FakeCapture();
        var r = new ViewCaptureService(fake).Capture(Doc, new CaptureRequest { Target = "active" });
        Assert.Single(r.Images);
        Assert.Equal("Perspective", r.Images[0].Viewport);
        Assert.Equal((1280, 720), (r.Images[0].Width, r.Images[0].Height));
        Assert.Empty(r.Notices);
    }

    [Fact]
    public void All_FansOutToEveryViewport()
    {
        var fake = new FakeCapture();
        var r = new ViewCaptureService(fake).Capture(Doc, new CaptureRequest { Target = "all", Zoom = "extents" });
        Assert.Equal(new[] { "Perspective", "Top", "Front" }, r.Images.Select(i => i.Viewport));
        Assert.All(fake.Calls, c => Assert.Equal("extents", c.Zoom));
    }

    [Fact]
    public void NamedViewport_IsCaseInsensitive()
    {
        var fake = new FakeCapture();
        var r = new ViewCaptureService(fake).Capture(Doc, new CaptureRequest { Target = "top" });
        Assert.Equal("Top", Assert.Single(r.Images).Viewport);
    }

    [Fact]
    public void UnknownViewport_ListsTheKnownOnes_AndTouchesNothing()
    {
        var fake = new FakeCapture();
        var ex = Assert.Throws<CaptureRequestException>(() => new ViewCaptureService(fake).Capture(Doc, new CaptureRequest { Target = "Left" }));
        Assert.Equal("viewport-not-found", ex.Record.Code);
        Assert.Contains("Perspective", ex.Record.Message);
        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData("zoom", "sideways")]
    [InlineData("display_mode", "Holographic")]
    public void InvalidOptions_AreRefusedBeforeAnyCapture(string param, string value)
    {
        var fake = new FakeCapture();
        var req = param == "zoom" ? new CaptureRequest { Target = "active", Zoom = value } : new CaptureRequest { Target = "active", DisplayMode = value };
        var ex = Assert.Throws<CaptureRequestException>(() => new ViewCaptureService(fake).Capture(Doc, req));
        Assert.Equal("invalid-param", ex.Record.Code);
        Assert.Equal(param, ex.Record.Detail["param"]);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void DisplayMode_IsMatchedCaseInsensitively_AndPassedCanonically()
    {
        var fake = new FakeCapture();
        new ViewCaptureService(fake).Capture(Doc, new CaptureRequest { Target = "active", DisplayMode = "shaded" });
        Assert.Equal("Shaded", fake.Calls[0].Mode);
    }

    [Fact]
    public void RestoreFailure_BecomesANotice_NotAFailure()
    {
        var fake = new FakeCapture { RestoreFailure = "PopViewProjection threw" };
        var r = new ViewCaptureService(fake).Capture(Doc, new CaptureRequest { Target = "active", Zoom = "extents" });
        Assert.Single(r.Images);
        var n = Assert.Single(r.Notices);
        Assert.Equal("capture-restore-failed", n.Code);
        Assert.Contains("PopViewProjection", n.Message);
    }

    [Fact]
    public void NoViewports_IsItsOwnError()
    {
        var fake = new FakeCapture { Names = new() };
        var ex = Assert.Throws<CaptureRequestException>(() => new ViewCaptureService(fake).Capture(Doc, new CaptureRequest { Target = "active" }));
        Assert.Equal("no-viewports", ex.Record.Code);
    }
}
