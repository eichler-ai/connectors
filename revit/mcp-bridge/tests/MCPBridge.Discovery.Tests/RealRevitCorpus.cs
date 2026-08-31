using System.IO;
using System.Reflection;
using MCPBridge.Core.Discovery;

namespace MCPBridge.Discovery.Tests;

/// <summary>
/// Builds the real Revit corpus the way PRODUCTION builds it, in one place.
///
/// <para>Exists because it was already going wrong twice. <c>RankingCorpusTests</c> originally synced
/// RevitAPI alone, which silently removed every <c>Autodesk.Revit.UI</c> member and made the ranking
/// corpus measure against something an agent never sees; and <c>TierBoundaryTests</c> then repeated that
/// exact mistake in a file sitting next to the comment warning about it. Two near-identical RevitAPIUI
/// loaders had also accumulated. A shared helper makes "the same corpus as production" a single fact
/// rather than a convention each new test file re-derives.</para>
///
/// <para>Mirrors <c>BridgeHost.CollectAssembliesToSync</c>: RevitAPI and RevitAPIUI both as
/// <c>"core"</c>. It does NOT add the connector's own assembly as <c>"addin"</c>, which production also
/// does since issue #91 -- seven members against ~26k, and loading it here would mean resolving a third
/// assembly into the metadata context for no measurable ranking effect. Tests that need an add-in
/// present build their own cache; see <see cref="AddInVisibilityTests"/>.</para>
/// </summary>
internal static class RealRevitCorpus
{
    /// <summary>
    /// The loaded metadata context plus a cache synced with both core assemblies, or null when this
    /// machine has no Revit for the TFM under test. Callers dispose both; the context must outlive the
    /// cache's use of anything reflected from it.
    /// </summary>
    public static (MetadataLoadContext Context, DiscoveryCache Cache)? TryBuild()
    {
        var loaded = RealRevitApiLoader.TryLoad();
        if (loaded is null)
        {
            return null;
        }

        var cache = new DiscoveryCache(":memory:");
        var ui = TryLoadUi(loaded.Value.Context, loaded.Value.Assembly);
        cache.Sync(ui is null
            ? new[] { ("core", loaded.Value.Assembly) }
            : new[] { ("core", loaded.Value.Assembly), ("core", ui) });

        return (loaded.Value.Context, cache);
    }

    /// <summary>RevitAPIUI from the same install, loaded into the same metadata context; null if absent.</summary>
    public static Assembly? TryLoadUi(MetadataLoadContext context, Assembly core)
    {
        var uiPath = Path.Combine(Path.GetDirectoryName(core.Location)!, "RevitAPIUI.dll");
        return File.Exists(uiPath) ? context.LoadFromAssemblyPath(uiPath) : null;
    }
}
