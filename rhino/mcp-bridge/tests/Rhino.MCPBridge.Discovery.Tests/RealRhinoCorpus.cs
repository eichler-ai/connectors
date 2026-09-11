using System;
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

    // Reflecting RhinoCommon + building the in-memory FTS is ~1.5s; the read-only query tests share one
    // corpus rather than paying that per case (review #294 m6). Never disposed — it lives for the test run.
    private static readonly Lazy<DiscoveryCache> _shared = new(Build);

    /// <summary>A single shared read-only corpus for tests that only query (never mutate) it.</summary>
    public static DiscoveryCache Shared => _shared.Value;
}
