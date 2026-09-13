using System;
using System.Collections.Generic;
using System.Threading;
using Grasshopper.Kernel;
using Rhino.MCPBridge.Core.Discovery;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// Reads the installed Grasshopper component catalog from <c>Grasshopper.Instances.ComponentServer</c> for the
/// discovery index (PRD §09, kind=grasshopper), including each component's input/output <b>ports</b>.
///
/// <para><b>Threading and cost are load-bearing here.</b> The FIRST access to <c>ComponentServer</c> makes
/// Grasshopper load all component files and build a WinForms loading banner, and enumerating proxies touches
/// the macOS layout engine — all of which MUST run on the main (UI) thread, or Rhino aborts with
/// <c>NSInternalInconsistencyException</c> (verified). So every access is marshalled to the UI thread, and the
/// read is gated on a Grasshopper document being open (so the ComponentServer is already initialised and our
/// access is cheap and side-effect-free).</para>
///
/// <para>Reading a component's ports requires instantiating its proxy (<c>CreateInstance</c>) to inspect
/// <c>Params.Input/Output</c>. Measured live: ~1200 proxies instantiate in ~0.5 s (worst single ~12 ms). That
/// is cheap in total but would be a visible ~0.5 s freeze in one UI hop, so the enrichment is done in
/// <b>bounded batches</b> — each a short UI-thread hop, yielding between — so no single hop stalls Rhino.</para>
/// </summary>
public static class GrasshopperCatalog
{
    /// <summary>Proxies instantiated per UI-thread hop. Sized so a hop is well under a frame's worth of time
    /// (≈ 0.4 ms/proxy typical), keeping Rhino responsive during the enrichment.</summary>
    private const int BatchSize = 100;

    /// <summary>A short pause between hops, on the background thread, so the UI thread gets turns to paint and
    /// handle input rather than processing our batches back to back.</summary>
    private static readonly TimeSpan HopGap = TimeSpan.FromMilliseconds(10);

    /// <summary>How long a single marshalled UI-thread hop waits before giving up (the main thread may be
    /// mid-command). A timed-out hop aborts the enrichment; the caller retries later.</summary>
    private static readonly TimeSpan UiHopTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Every non-obsolete component/parameter proxy with its ports (metadata + inputs/outputs), or
    /// empty when Grasshopper is not loaded, not yet in use (no open definition), or a UI-thread hop could not
    /// complete in time.</summary>
    public static IReadOnlyList<GrasshopperCatalogEntry> ReadComponents()
    {
        if (!GrasshopperWatcher.GrasshopperLoaded())
        {
            return Array.Empty<GrasshopperCatalogEntry>();
        }

        // Hop 1: snapshot the proxy list on the UI thread (metadata only, no instantiation). Empty when
        // Grasshopper is not genuinely in use — do not force ComponentServer init from here.
        var proxies = RunOnUi(SnapshotProxies);
        if (proxies is null || proxies.Count == 0)
        {
            return Array.Empty<GrasshopperCatalogEntry>();
        }

        // Enrich ports in bounded batches, each a short UI hop, yielding between so Rhino stays responsive.
        var entries = new List<GrasshopperCatalogEntry>(proxies.Count);
        for (var i = 0; i < proxies.Count; i += BatchSize)
        {
            var count = Math.Min(BatchSize, proxies.Count - i);
            var batch = proxies.GetRange(i, count);
            var built = RunOnUi(() => BuildBatch(batch));
            if (built is null)
            {
                // A hop timed out: abandon this round with what we have; the sync loop retries later.
                break;
            }

            entries.AddRange(built);
            if (i + count < proxies.Count)
            {
                Thread.Sleep(HopGap);
            }
        }

        return entries;
    }

    /// <summary>Marshals fn to the UI thread and blocks (with a timeout) for its result, or default(T) on
    /// timeout. Reached only past the GrasshopperLoaded guard, always from the background discovery thread, so
    /// the marshal never self-deadlocks.</summary>
    private static T? RunOnUi<T>(Func<T> fn)
    {
        T? result = default;
        // NOT disposed with `using`: if Wait times out, this method returns while the callback is still
        // queued; when it later runs it calls Set(), which must not hit a disposed handle (an unhandled
        // UI-thread exception aborts Rhino). Leaving it for GC is safe and cheap. Set() is also guarded.
        var done = new ManualResetEventSlim(false);
        global::Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
        {
            try { result = fn(); }
            catch { /* leave default; the catalog just is not enriched this round */ }
            finally { try { done.Set(); } catch { /* handle raced with GC; ignore */ } }
        }));

        return done.Wait(UiHopTimeout) ? result : default;
    }

    /// <summary>On the UI thread. Returns the non-null, non-obsolete proxies to enrich — but only when a
    /// Grasshopper document is open (so Grasshopper is genuinely in use); otherwise null, so we never force
    /// ComponentServer init.</summary>
    private static List<IGH_ObjectProxy>? SnapshotProxies()
    {
        var docServer = global::Grasshopper.Instances.DocumentServer;
        if (docServer is null || docServer.DocumentCount == 0)
        {
            return null;
        }

        var server = global::Grasshopper.Instances.ComponentServer;
        if (server?.ObjectProxies is null)
        {
            return new List<IGH_ObjectProxy>();
        }

        var proxies = new List<IGH_ObjectProxy>();
        foreach (var proxy in server.ObjectProxies)
        {
            if (proxy is null || proxy.Obsolete)
            {
                continue;
            }

            proxies.Add(proxy);
        }

        return proxies;
    }

    /// <summary>On the UI thread. Builds one batch of catalog entries: description metadata for each proxy,
    /// plus its ports read by instantiating the component (the enrichment). A proxy that throws from its
    /// metadata or instantiation is skipped or degraded, never aborting the batch.</summary>
    private static List<GrasshopperCatalogEntry> BuildBatch(List<IGH_ObjectProxy> batch)
    {
        var entries = new List<GrasshopperCatalogEntry>(batch.Count);
        foreach (var proxy in batch)
        {
            try
            {
                var desc = proxy.Desc;
                if (desc is null || string.IsNullOrWhiteSpace(desc.Name))
                {
                    continue;
                }

                var (inputs, outputs) = ReadPorts(proxy);
                entries.Add(new GrasshopperCatalogEntry(
                    proxy.Guid.ToString(),
                    desc.Name,
                    desc.NickName ?? "",
                    desc.Category ?? "",
                    desc.SubCategory ?? "",
                    desc.Description,
                    inputs,
                    outputs));
            }
            catch
            {
                // A proxy that throws from any getter is skipped; index the rest.
            }
        }

        return entries;
    }

    /// <summary>Instantiates a component proxy and reads its input/output parameter ports (name + data type).
    /// A parameter (not a component) has no such ports; anything that throws degrades to empty ports so the
    /// entry is still indexed by description.</summary>
    private static (IReadOnlyList<GrasshopperPort> Inputs, IReadOnlyList<GrasshopperPort> Outputs) ReadPorts(IGH_ObjectProxy proxy)
    {
        try
        {
            if (proxy.CreateInstance() is IGH_Component component)
            {
                return (PortsOf(component.Params.Input), PortsOf(component.Params.Output));
            }
        }
        catch
        {
            // Fall through to empty ports; the component is still listed by its description.
        }

        return (Array.Empty<GrasshopperPort>(), Array.Empty<GrasshopperPort>());
    }

    private static IReadOnlyList<GrasshopperPort> PortsOf(IList<IGH_Param> parameters)
    {
        var ports = new List<GrasshopperPort>(parameters.Count);
        foreach (var p in parameters)
        {
            if (p is null)
            {
                continue;
            }

            var name = string.IsNullOrEmpty(p.Name) ? p.NickName : p.Name;
            ports.Add(new GrasshopperPort(name ?? "", SafeTypeName(p), null));
        }

        return ports;
    }

    private static string SafeTypeName(IGH_Param p)
    {
        try { return p.TypeName ?? ""; }
        catch { return ""; }
    }
}
