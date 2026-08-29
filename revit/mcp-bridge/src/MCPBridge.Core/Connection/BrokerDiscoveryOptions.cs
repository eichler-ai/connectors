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

    // A remote-mode fallback host:port (MCPBRIDGE_FALLBACK_HOST/PORT) used to live here, per an
    // earlier reading of PRD §05's "falls back to a configured broker_host:port only if no shared
    // drive exists". It was removed (v1 integrated review) because it could never work: auth is
    // mandatory on first message (PRD §10) and the token exists only in broker.json, so a
    // fallback address with no broker.json to read the token from was discarded unconnected by
    // BridgeHost every time -- dead configuration that misled anyone setting it. If a real
    // no-shared-drive remote topology ever materializes, it needs a genuine auth story first.

    private BrokerDiscoveryOptions(BrokerTopologyMode mode, string connectorRoot)
    {
        Mode = mode;
        ConnectorRoot = connectorRoot;
    }

    /// <summary>
    /// Local mode: %LOCALAPPDATA%\Connectors\Revit\broker.json, per the app-data layout
    /// convention in PRD §09 / CONVENTIONS.md. <paramref name="localAppDataRoot"/> lets
    /// tests substitute a temp directory for %LOCALAPPDATA%.
    /// </summary>
    public static BrokerDiscoveryOptions Local(string? localAppDataRoot = null)
    {
        var root = localAppDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new BrokerDiscoveryOptions(BrokerTopologyMode.Local, Path.Combine(root, "Connectors", "Revit"));
    }

    /// <summary>
    /// Remote mode: &lt;sharedRootUncPath&gt;\Connectors\Revit\broker.json (PRD §05/§09). Must
    /// be given as a UNC path (\\host\share) -- a mapped drive letter is rejected, since
    /// letter assignment isn't guaranteed stable in this project's own dev environment.
    /// </summary>
    public static BrokerDiscoveryOptions Remote(string sharedRootUncPath)
    {
        if (string.IsNullOrWhiteSpace(sharedRootUncPath) || !sharedRootUncPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Remote-mode discovery requires a UNC path (\\\\host\\share), not a mapped drive letter -- see PRD §09.",
                nameof(sharedRootUncPath));
        }

        var root = Path.Combine(sharedRootUncPath, "Connectors", "Revit");
        return new BrokerDiscoveryOptions(BrokerTopologyMode.Remote, root);
    }
}
