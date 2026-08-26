using System.Collections.Generic;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// v1 non-framework-dialog fallback (PRD §07): diagnosis only, no auto-action. First P/Invoke boundary
/// in this repo -- kept behind this interface, implemented by Win32WindowInventory, so MCPBridge.Core
/// stays testable against a fake, matching the IDocumentAdapter/ITransactionAdapter seam pattern.
/// </summary>
public interface IWindowInventory
{
    IReadOnlyList<WindowInfo> EnumerateOwnedTopLevelWindows();
}

/// <summary>
/// ChildText is best-effort: static/label/button control text collected from a window's immediate
/// children (EnumChildWindows + GetWindowText). Empty for owner-drawn/canvas-rendered dialogs (WPF and
/// some custom WinForms controls don't expose text this way) -- Title remains available as a fallback
/// even when child-text extraction comes back empty.
/// </summary>
public sealed record WindowInfo(string Title, string ClassName, IReadOnlyList<string> ChildText);
