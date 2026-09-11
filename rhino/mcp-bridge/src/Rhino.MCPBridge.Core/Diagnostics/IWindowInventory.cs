using System.Collections.Generic;

namespace Rhino.MCPBridge.Core.Diagnostics;

/// <summary>
/// PRD §08 v1 modal-dialog diagnostic — diagnosis only, takes no action (the auto-dismiss surface the
/// Revit connector grew is deliberately not ported). Enumerates this process's owned top-level windows so
/// the §08 timeout fallback (<see cref="WindowInventoryDiagnostic"/>) can report a window that may be
/// blocking a run that has not finished.
///
/// <para><b>MUST run off Rhino's main thread.</b> The whole point of the fallback is that the main thread
/// may be blocked — a modal dialog or a command-line prompt is the thing being diagnosed — so an
/// implementation may not marshal to it (it would hang on exactly what it is inspecting). Win32
/// <c>EnumWindows</c> and macOS <c>CGWindowListCopyWindowInfo</c> both enumerate without the main thread;
/// the dispatcher calls this on the connection thread, never through the main-thread hop that
/// <c>capture_view</c> uses. Behind this seam so Core stays testable against a fake (the IViewCapture
/// pattern).</para>
/// </summary>
public interface IWindowInventory
{
    /// <summary>Enumerates this process's owned top-level windows. Never throws for the caller: an
    /// implementation reports what it could gather and sets <see cref="WindowInventorySnapshot.Truncated"/>
    /// if it stopped early (a time budget, a failure); the caller degrades to a plain result.</summary>
    WindowInventorySnapshot EnumerateOwnedTopLevelWindows();
}

/// <summary>
/// One enumeration pass. <see cref="Truncated"/> is PRD §01 honesty: the real implementation stops once an
/// overall time budget lapses (reading window text against a busy UI thread costs wall-clock), and a
/// half-enumerated inventory presented as complete would have an agent conclude a window is absent when it
/// merely was not reached — so the §08 notice says the list was truncated when it was.
/// </summary>
public sealed record WindowInventorySnapshot(IReadOnlyList<WindowInfo> Windows, bool Truncated);

/// <summary>
/// An owned top-level window. <see cref="IsMainWindow"/> marks Rhino's own main frame (on Windows, the
/// window matching the process <c>MainWindowHandle</c>) so <see cref="WindowInventoryDiagnostic"/> reports
/// only CANDIDATE windows — a visible top-level window that is not the main one, i.e. a possible modal
/// blocking the run — and stays silent when the only window is Rhino itself (high-signal or silent).
/// Capability-free: strings and bools only, so this public, script-reachable type exposes nothing an
/// untrusted script could act on.
/// </summary>
public sealed record WindowInfo(string Title, string ClassName, bool IsMainWindow, bool IsVisible);
