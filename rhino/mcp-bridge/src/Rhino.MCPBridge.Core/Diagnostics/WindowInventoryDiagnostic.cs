using System;
using System.Collections.Generic;
using System.Linq;

namespace Rhino.MCPBridge.Core.Diagnostics;

/// <summary>
/// PRD §08 v1: turns a <see cref="WindowInventorySnapshot"/> into the timeout-fallback notice(s) attached
/// to a run that has not finished. The policy — which windows count as a candidate blocking dialog — lives
/// here in Core so it is unit-testable over a fake inventory, independent of the P/Invoke that gathers the
/// windows.
///
/// <para><b>High-signal or silent.</b> The dispatcher reaches this on every timeout that leaves a run
/// non-terminal, which is also the normal "here is your running handle" path for any long compute. So a
/// notice is produced ONLY when a candidate window is present — a visible top-level window that is not
/// Rhino's own main window; when the only window is Rhino itself, nothing is attached, so the notice never
/// cries wolf on a long run that is not actually blocked.</para>
/// </summary>
public static class WindowInventoryDiagnostic
{
    /// <summary>The §08 v1 notice code. Info severity: it is a hint for a run that may be blocked, not an
    /// error — the run may yet finish. Same code the ported wire plumbing already round-trips
    /// (ExecutionResultMessage / FromRecord extraNotices).</summary>
    public const string NoticeCode = "window-inventory-timeout-fallback";

    /// <summary>A candidate blocking window: visible, not Rhino's own main frame, and with a non-empty
    /// title. The main window and hidden/message-only windows are excluded, and so are titleless
    /// infrastructure top-levels (WPF HwndWrapper, IME, GDI+, DDE, ComboLBox and the like — verified live
    /// that an idle Rhino owns ~20 of these): a modal or prompt actually blocking a person has a caption,
    /// so requiring one keeps the notice high-signal rather than firing on every long run (PRD §08). The
    /// tradeoff, stated: a hypothetical titleless modal would be missed here; the timeout itself still
    /// surfaces, and this is a best-effort diagnostic hint, not a guarantee.</summary>
    internal static bool IsCandidate(WindowInfo w) =>
        w.IsVisible && !w.IsMainWindow && !string.IsNullOrWhiteSpace(w.Title);

    /// <summary>The §08 timeout-fallback notices for a snapshot, or an empty list when no candidate window
    /// is present (see the class remark on high-signal-or-silent). At most one Info notice, from
    /// <see cref="DiagnosticSource.Dialogs"/>, listing the candidate windows' class and title; a truncated
    /// enumeration is stated so an absent window is not read as proof of absence (PRD §01).</summary>
    public static IReadOnlyList<DiagnosticRecord> BuildTimeoutNotices(WindowInventorySnapshot snapshot)
    {
        var candidates = snapshot.Windows.Where(IsCandidate).ToList();
        if (candidates.Count == 0)
        {
            return Array.Empty<DiagnosticRecord>();
        }

        var listed = string.Join("; ", candidates.Select(w => $"'{w.Title}' ({w.ClassName})"));
        var message = $"This run has not finished and Rhino has {candidates.Count} top-level window(s) open " +
                      $"besides its main window — one may be a modal dialog or a command-line prompt blocking it: {listed}." +
                      (snapshot.Truncated ? " The window list was truncated at the enumeration budget, so it may be incomplete." : "");

        var detail = new Dictionary<string, object?>
        {
            ["windows"] = candidates.Select(w => new Dictionary<string, object?>
            {
                ["title"] = w.Title,
                ["class_name"] = w.ClassName,
            }).ToList(),
            ["truncated"] = snapshot.Truncated,
        };

        var remedy = new[]
        {
            "Look at Rhino's window: a dialog or a command-line prompt may be waiting for input. Answer or dismiss it, or cancel the run with cancel_execution.",
            "Scripts should avoid interactive input (the interactive getters are already refused, PRD §08); supply inputs programmatically instead.",
        };

        return new[] { DiagnosticRecord.Create(DiagnosticSeverity.Info, NoticeCode, DiagnosticSource.Dialogs, message, detail, remedy) };
    }
}
