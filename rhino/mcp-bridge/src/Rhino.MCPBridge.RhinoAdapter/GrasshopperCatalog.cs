using System;
using System.Collections.Generic;
using System.Threading;
using Grasshopper.Kernel;
using Rhino.MCPBridge.Core.Discovery;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// Reads the installed Grasshopper component catalog from <c>Grasshopper.Instances.ComponentServer</c> for the
/// discovery index (PRD §09, kind=grasshopper).
///
/// <para><b>Threading and lazy-init are load-bearing here.</b> The FIRST access to <c>ComponentServer</c>
/// makes Grasshopper load all component files and build a WinForms loading banner, and enumerating proxies
/// touches the macOS layout engine — all of which MUST run on the main (UI) thread, or Rhino aborts with
/// <c>NSInternalInconsistencyException</c> (verified: a background read crashed Rhino). So the whole read is
/// marshalled to the UI thread. To avoid FORCING that heavy init when the user is not using Grasshopper, the
/// read is gated on a Grasshopper document being open — which means the ComponentServer is already initialised,
/// so our access is cheap and side-effect-free. v1 reads catalog metadata only (Desc), not per-component
/// ports: instantiating every proxy to read its ports would hang the UI thread.</para>
/// </summary>
public static class GrasshopperCatalog
{
    /// <summary>How long the background caller waits for the marshalled UI-thread read before giving up (it
    /// retries later). Generous, since the main thread may be mid-command.</summary>
    private static readonly TimeSpan UiReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Every non-obsolete component/parameter proxy (metadata only), or empty when Grasshopper is not
    /// loaded, not yet in use (no open definition), or the UI-thread read could not complete in time.</summary>
    public static IReadOnlyList<GrasshopperCatalogEntry> ReadComponents()
    {
        if (!GrasshopperWatcher.GrasshopperLoaded())
        {
            return Array.Empty<GrasshopperCatalogEntry>();
        }

        return ReadOnUiThread();
    }

    /// <summary>Marshals the Grasshopper-typed read to the UI thread and blocks (with a timeout) for it. Only
    /// reached past the GrasshopperLoaded guard.</summary>
    private static IReadOnlyList<GrasshopperCatalogEntry> ReadOnUiThread()
    {
        IReadOnlyList<GrasshopperCatalogEntry> result = Array.Empty<GrasshopperCatalogEntry>();
        using var done = new ManualResetEventSlim(false);
        global::Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
        {
            try { result = ReadCoreIfInUse(); }
            catch { /* leave empty; the catalog just is not indexed this round */ }
            finally { done.Set(); }
        }));

        done.Wait(UiReadTimeout);
        return result;
    }

    /// <summary>On the UI thread. Reads the catalog only when a Grasshopper document is open, so the
    /// ComponentServer is already initialised (touching it otherwise triggers the loading-UI init).</summary>
    private static IReadOnlyList<GrasshopperCatalogEntry> ReadCoreIfInUse()
    {
        var docServer = global::Grasshopper.Instances.DocumentServer;
        if (docServer is null || docServer.DocumentCount == 0)
        {
            return Array.Empty<GrasshopperCatalogEntry>(); // GH not in use yet — do not force ComponentServer init
        }

        var server = global::Grasshopper.Instances.ComponentServer;
        var entries = new List<GrasshopperCatalogEntry>();
        if (server?.ObjectProxies is null)
        {
            return entries;
        }

        foreach (var proxy in server.ObjectProxies)
        {
            try
            {
                if (proxy is null || proxy.Obsolete)
                {
                    continue;
                }

                var desc = proxy.Desc;
                if (desc is null || string.IsNullOrWhiteSpace(desc.Name))
                {
                    continue;
                }

                entries.Add(new GrasshopperCatalogEntry(
                    proxy.Guid.ToString(),
                    desc.Name,
                    desc.NickName ?? "",
                    desc.Category ?? "",
                    desc.SubCategory ?? "",
                    desc.Description,
                    Array.Empty<GrasshopperPort>(),
                    Array.Empty<GrasshopperPort>()));
            }
            catch
            {
                // A proxy that throws from any of its metadata getters is skipped; index the rest.
            }
        }

        return entries;
    }
}
