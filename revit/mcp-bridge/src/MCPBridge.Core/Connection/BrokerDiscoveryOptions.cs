using System;
using System.IO;

namespace MCPBridge.Core.Connection;

/// <summary>
/// Where to look for broker.json, per PRD §05's local/remote topology table.
/// Local mode reads a Windows app-data path; remote mode reads the shared drive's
/// agreed root via its UNC path (never a mapped drive letter -- PRD §09 explains
/// why: letter assignment isn't guaranteed stable across reboots).
/// </summary>
public sealed class BrokerDiscoveryOptions
{
    public BrokerTopologyMode Mode { get; }

    /// <summary>Root directory searched for broker.json (already namespaced to Connectors\Revit\ by the factory methods).</summary>
    public string ConnectorRoot { get; }

    /// <summary>Remote-mode-only: explicit host:port to fall back to when no shared drive is reachable (PRD §05).</summary>
    public string? FallbackHost { get; }
    public int? FallbackPort { get; }

    private BrokerDiscoveryOptions(BrokerTopologyMode mode, string connectorRoot, string? fallbackHost, int? fallbackPort)
    {
        Mode = mode;
        ConnectorRoot = connectorRoot;
        FallbackHost = fallbackHost;
        FallbackPort = fallbackPort;
    }

    /// <summary>
    /// Local mode: %LOCALAPPDATA%\Connectors\Revit\broker.json, per the app-data layout
    /// convention in PRD §09 / CONVENTIONS.md. <paramref name="localAppDataRoot"/> lets
    /// tests substitute a temp directory for %LOCALAPPDATA%.
    /// </summary>
    public static BrokerDiscoveryOptions Local(string? localAppDataRoot = null)
    {
        var root = localAppDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new BrokerDiscoveryOptions(BrokerTopologyMode.Local, Path.Combine(root, "Connectors", "Revit"), null, null);
    }

    /// <summary>
    /// Remote mode: &lt;sharedRootUncPath&gt;\Connectors\Revit\broker.json (PRD §05/§09). Must
    /// be given as a UNC path (\\host\share) -- a mapped drive letter is rejected, since
    /// letter assignment isn't guaranteed stable in this project's own dev environment.
    /// </summary>
    public static BrokerDiscoveryOptions Remote(string sharedRootUncPath, string? fallbackHost = null, int? fallbackPort = null)
    {
        if (string.IsNullOrWhiteSpace(sharedRootUncPath) || !sharedRootUncPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Remote-mode discovery requires a UNC path (\\\\host\\share), not a mapped drive letter -- see PRD §09.",
                nameof(sharedRootUncPath));
        }

        var root = Path.Combine(sharedRootUncPath, "Connectors", "Revit");
        return new BrokerDiscoveryOptions(BrokerTopologyMode.Remote, root, fallbackHost, fallbackPort);
    }
}
