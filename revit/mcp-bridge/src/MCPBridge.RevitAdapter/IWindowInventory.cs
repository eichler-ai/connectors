using System.Collections.Generic;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// v1 non-framework-dialog fallback (PRD §07): diagnosis only, no auto-action. First P/Invoke boundary
/// in this repo -- kept behind this interface, implemented by Win32WindowInventory, so MCPBridge.Core
/// stays testable against a fake, matching the IDocumentAdapter/ITransactionAdapter seam pattern.
/// </summary>
public interface IWindowInventory
{
    WindowInventorySnapshot EnumerateOwnedTopLevelWindows();
}

/// <summary>
/// One enumeration pass's result. Truncated is PRD §01 honesty (independent PR review finding):
/// the real implementation stops enumerating once its overall time budget lapses (see
/// Win32WindowInventory.OverallBudgetMs), and a half-enumerated inventory presented as complete
/// would have an agent -- or a human triaging a stuck dialog -- conclude a window doesn't exist
/// when it merely wasn't reached. When Truncated is true, the §07 fallback notice says so.
/// </summary>
public sealed record WindowInventorySnapshot(IReadOnlyList<WindowInfo> Windows, bool Truncated);

/// <summary>
/// ChildText is best-effort: static/label/button control text collected from a window's immediate
/// children (EnumChildWindows + GetWindowText). Empty for owner-drawn/canvas-rendered dialogs (WPF and
/// some custom WinForms controls don't expose text this way) -- Title remains available as a fallback
/// even when child-text extraction comes back empty.
/// </summary>
public sealed record WindowInfo(string Title, string ClassName, IReadOnlyList<string> ChildText);
