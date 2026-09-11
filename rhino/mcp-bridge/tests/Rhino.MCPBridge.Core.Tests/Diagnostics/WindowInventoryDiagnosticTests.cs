using System;
using System.Collections.Generic;
using Rhino.MCPBridge.Core.Diagnostics;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Diagnostics;

/// <summary>The §08 v1 candidate policy + notice-builder (PRD §08). The rule under test: high-signal or
/// silent — a notice only when a visible top-level window that is NOT Rhino's own main window is present.</summary>
public sealed class WindowInventoryDiagnosticTests
{
    private static WindowInfo MainWindow(string title = "Untitled - Rhinoceros") =>
        new(title, "RhinoMainWnd", IsMainWindow: true, IsVisible: true);

    private static WindowInfo Dialog(string title, string className = "#32770", bool visible = true) =>
        new(title, className, IsMainWindow: false, IsVisible: visible);

    [Fact]
    public void OnlyTheMainWindow_ProducesNoNotice()
    {
        var snapshot = new WindowInventorySnapshot(new[] { MainWindow() }, Truncated: false);
        Assert.Empty(WindowInventoryDiagnostic.BuildTimeoutNotices(snapshot));
    }

    [Fact]
    public void NoWindowsAtAll_ProducesNoNotice()
    {
        var snapshot = new WindowInventorySnapshot(Array.Empty<WindowInfo>(), Truncated: false);
        Assert.Empty(WindowInventoryDiagnostic.BuildTimeoutNotices(snapshot));
    }

    [Fact]
    public void HiddenNonMainWindow_IsNotACandidate()
    {
        // An invisible owned window (e.g. a message-only or hidden helper) must not be flagged.
        var snapshot = new WindowInventorySnapshot(new[] { MainWindow(), Dialog("hidden helper", "SomeHiddenClass", visible: false) }, Truncated: false);
        Assert.Empty(WindowInventoryDiagnostic.BuildTimeoutNotices(snapshot));
    }

    [Fact]
    public void VisibleTitlelessInfrastructureWindow_IsNotACandidate()
    {
        // Verified live: an idle Rhino owns a visible, empty-title WPF HwndWrapper top-level window. It
        // must not be flagged as a blocking dialog — the false positive the §08 live smoke caught.
        var infra = new WindowInfo("", "HwndWrapper[DefaultDomain;;765b0558]", IsMainWindow: false, IsVisible: true);
        var snapshot = new WindowInventorySnapshot(new[] { MainWindow(), infra }, Truncated: false);
        Assert.Empty(WindowInventoryDiagnostic.BuildTimeoutNotices(snapshot));
    }

    [Fact]
    public void ACandidateDialog_ProducesExactlyOneInfoDialogsNotice_ThatNamesIt()
    {
        var snapshot = new WindowInventorySnapshot(new[] { MainWindow(), Dialog("Open Template File") }, Truncated: false);

        var notice = Assert.Single(WindowInventoryDiagnostic.BuildTimeoutNotices(snapshot));
        Assert.Equal(WindowInventoryDiagnostic.NoticeCode, notice.Code);
        Assert.Equal(DiagnosticSeverity.Info, notice.Severity);
        Assert.Equal("mcp-bridge.core.dialogs", notice.Source); // DiagnosticSource.Dialogs tag
        Assert.Contains("Open Template File", notice.Message);
        Assert.Contains("#32770", notice.Message);
        Assert.NotEmpty(notice.Remedy);
        Assert.True(notice.Detail.ContainsKey("windows"));
        Assert.False((bool)notice.Detail["truncated"]!);
    }

    [Fact]
    public void MultipleCandidates_AreAllListed()
    {
        var snapshot = new WindowInventorySnapshot(new[] { MainWindow(), Dialog("A"), Dialog("B", "EtoDialog") }, Truncated: false);
        var notice = Assert.Single(WindowInventoryDiagnostic.BuildTimeoutNotices(snapshot));
        Assert.Contains("'A'", notice.Message);
        Assert.Contains("'B'", notice.Message);
        Assert.Contains("2 top-level window", notice.Message);
    }

    [Fact]
    public void ATruncatedPass_SaysSo_SoAnAbsentWindowIsNotReadAsAbsence()
    {
        var snapshot = new WindowInventorySnapshot(new[] { MainWindow(), Dialog("Save changes?") }, Truncated: true);
        var notice = Assert.Single(WindowInventoryDiagnostic.BuildTimeoutNotices(snapshot));
        Assert.Contains("truncated", notice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True((bool)notice.Detail["truncated"]!);
    }

    [Fact]
    public void TruncatedButNoCandidate_StillProducesNoNotice()
    {
        // Truncation alone is not a reason to speak; only a candidate window is.
        var snapshot = new WindowInventorySnapshot(new[] { MainWindow() }, Truncated: true);
        Assert.Empty(WindowInventoryDiagnostic.BuildTimeoutNotices(snapshot));
    }
}
