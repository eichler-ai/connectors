using Rhino.MCPBridge.Core.Discovery;

namespace Rhino.MCPBridge.Discovery.Tests;

/// <summary>
/// Builds the real RhinoCommon corpus the way production does (<c>DiscoveryBootstrap.CollectAssembliesToSync</c>:
/// RhinoCommon as <c>"core"</c>), in one place. Unlike the Revit connector's <c>RealRevitCorpus</c>, this needs
/// no <c>MetadataLoadContext</c> and never returns null: RhinoCommon is a pure-managed NuGet assembly that loads
/// for execution directly, so these tests always run rather than self-skipping when an install is absent.
/// </summary>
internal static class RealRhinoCorpus
{
    public static DiscoveryCache Build()
    {
        var cache = new DiscoveryCache(":memory:");
        cache.Sync(new[] { ("core", typeof(global::Rhino.RhinoDoc).Assembly) });
        return cache;
    }
}
