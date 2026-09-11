using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Rhino.MCPBridge.Core.Connection;
using Rhino.MCPBridge.Core.Discovery;

namespace Rhino.MCPBridge.PlugIn;

/// <summary>
/// Builds the API-discovery cache for this Rhino process (PRD §09), the Rhino counterpart of the Revit
/// add-in's discovery setup. Reflects RhinoCommon ("core") plus this connector's own API and any
/// third-party plug-ins loaded into the process ("addin") into a persistent SQLite cache under
/// <c>Connectors/Rhino/&lt;rhino-version&gt;/discovery-cache.db</c>, so the reflection cost (~1.5 s the
/// first time) is paid once and later launches sync only deltas. The cache is opened with one self-heal
/// attempt; a discovery failure never takes the bridge down — execute_script and everything else run
/// with no dependency on it, and <see cref="Rhino.MCPBridge.Core.Dispatch.RequestDispatcher"/> accepts a
/// null <see cref="DiscoveryService"/>.
/// </summary>
internal static class DiscoveryBootstrap
{
    /// <summary>Opens (self-healing once) and syncs the cache, returning a live service plus the cache to
    /// dispose on teardown, or (null, null) when discovery could not be brought up.</summary>
    public static (DiscoveryService? Service, DiscoveryCache? Cache) Create(string rhinoVersion, Action<string> log)
    {
        var dbPath = Path.Combine(AppDataPaths.ConnectorRoot(), rhinoVersion, "discovery-cache.db");
        DiscoveryCache? cache;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            cache = new DiscoveryCache(dbPath);
        }
        catch (Exception ex)
        {
            log($"discovery cache open FAILED, attempting one self-heal (delete + recreate): {ex.Message}");
            try
            {
                File.Delete(dbPath);
                cache = new DiscoveryCache(dbPath);
            }
            catch (Exception retry)
            {
                log($"discovery cache self-heal FAILED; discovery disabled for this session: {retry.Message}");
                return (null, null);
            }
        }

        try
        {
            var result = cache.Sync(CollectAssembliesToSync(log));
            log($"discovery cache sync: added={result.Added} updated={result.Updated} removed={result.Removed} unchanged={result.Unchanged}");
        }
        catch (Exception ex)
        {
            // A sync failure leaves the cache usable with whatever it already held; do not fail the bridge.
            log($"discovery cache sync FAILED (discovery serves the prior cache, if any): {ex.Message}");
        }

        return (new DiscoveryService(cache), cache);
    }

    /// <summary>
    /// core = RhinoCommon (the one assembly PRD §09 calls "core" for v1; Grasshopper joins as its own kind
    /// in phase 4). addin = this connector's own <c>Eichler.Connectors.Rhino</c> API, plus every other
    /// plug-in loaded into the process that is NOT one of Rhino's own bundled assemblies.
    ///
    /// <para>Rhino's own DLLs — RhinoCommon, Eto, IronPython, and every bundled ManagedPlugIn (Grasshopper,
    /// RhinoCodePlugin, RhinoCycles, …) — all live under the Rhino install root (the <c>.app</c> bundle on
    /// macOS, the install folder on Windows), verified live. A third-party plug-in loads from elsewhere
    /// (the yak packages folder), so "under the Rhino install root" is the exclusion signal, the Rhino
    /// analogue of the Revit connector's single-install-dir heuristic. Our own infrastructure assemblies
    /// (<c>Rhino.MCPBridge.*</c>) are excluded by name; <c>Eichler.Connectors.Rhino</c> is added by hand so
    /// its one public <see cref="Eichler.Connectors.Rhino.Connector"/> type is indexed regardless of load
    /// timing.</para>
    /// </summary>
    private static IReadOnlyList<(string Kind, Assembly Assembly)> CollectAssembliesToSync(Action<string> log)
    {
        var rhinoCommon = typeof(global::Rhino.RhinoDoc).Assembly;
        var assemblies = new List<(string Kind, Assembly Assembly)> { ("core", rhinoCommon) };
        assemblies.Add(("addin", typeof(global::Eichler.Connectors.Rhino.Connector).Assembly));

        var rhinoRoot = RhinoInstallRoot(rhinoCommon.Location);
        var excludedPrefixes = new[] { "System.", "Microsoft.", "Rhino.MCPBridge.", "mscorlib", "netstandard" };
        var excludedByRoot = new List<string>();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(assembly.Location))
            {
                continue;
            }

            if (excludedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            {
                continue;
            }

            if (assemblies.Any(a => string.Equals(a.Assembly.Location, assembly.Location, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // already added (core, or the connector's own API)
            }

            if (rhinoRoot is not null && assembly.Location.StartsWith(rhinoRoot, StringComparison.OrdinalIgnoreCase))
            {
                excludedByRoot.Add(name);
                continue; // Rhino's own bundled assemblies — not a third-party add-in
            }

            assemblies.Add(("addin", assembly));
        }

        if (excludedByRoot.Count > 0)
        {
            log($"discovery: excluded {excludedByRoot.Count} assembly(ies) under the Rhino install root ({rhinoRoot}): {string.Join(", ", excludedByRoot)}");
        }

        return assemblies;
    }

    /// <summary>The Rhino install root that all of Rhino's own assemblies sit under: the <c>.app</c> bundle
    /// on macOS (e.g. <c>/Applications/Rhino 8.app</c>), or the install folder on Windows (RhinoCommon sits
    /// in <c>&lt;root&gt;\System\</c>, so the root is that directory's parent). Derived from RhinoCommon's
    /// own location so it is correct wherever Rhino was installed. Null if it cannot be determined.</summary>
    internal static string? RhinoInstallRoot(string rhinoCommonLocation)
    {
        if (string.IsNullOrEmpty(rhinoCommonLocation))
        {
            return null;
        }

        var appIdx = rhinoCommonLocation.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        if (appIdx < 0)
        {
            appIdx = rhinoCommonLocation.IndexOf(".app\\", StringComparison.OrdinalIgnoreCase);
        }

        if (appIdx >= 0)
        {
            return rhinoCommonLocation.Substring(0, appIdx + ".app".Length);
        }

        // Windows/Linux: RhinoCommon lives in <root>\System\; the root is that directory's parent.
        var dir = Path.GetDirectoryName(rhinoCommonLocation);
        return dir is null ? null : (Path.GetDirectoryName(dir) ?? dir);
    }
}
