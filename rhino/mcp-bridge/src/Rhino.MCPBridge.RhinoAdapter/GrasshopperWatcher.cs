using System.Linq;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// Keeps the register snapshot (and so <c>list_instances</c>) in step with Grasshopper's open-definition
/// set, so a definition appears the moment it is opened and disappears when closed — not only after an
/// unrelated Rhino document event (review of PR #304). Without this, a user who opens Grasshopper and
/// loads a <c>.gh</c> would not see it until some Rhino-doc event or connector run happened to fire.
///
/// <para>Grasshopper is demand-loaded, so this polls <c>RhinoApp.Idle</c> (throttled to about once a
/// second, never every idle tick) until Grasshopper's assembly is in the process, then subscribes ONCE to
/// its <c>DocumentServer</c> add/remove events and stops polling. All Grasshopper-typed code lives in
/// <see cref="Subscribe"/> so <c>Grasshopper.dll</c> is resolved only after the assembly-loaded guard has
/// passed — the same isolation <see cref="RhinoDocumentSnapshotSource"/> uses.</para>
/// </summary>
internal static class GrasshopperWatcher
{
    private static bool _subscribed;

    /// <summary>Begins watching. <paramref name="onChanged"/> is invoked (on the main thread) whenever a
    /// Grasshopper definition is added or removed. Idempotent: a second call is a no-op once subscribed.</summary>
    public static void Start(Action onChanged, Action<string> log)
    {
        var lastCheck = DateTime.MinValue;
        EventHandler? onIdle = null;
        onIdle = (_, _) =>
        {
            var now = DateTime.UtcNow;
            if ((now - lastCheck).TotalSeconds < 1)
            {
                return; // throttle: a cheap assembly check about once a second, not on every idle tick
            }

            lastCheck = now;
            if (!GrasshopperLoaded())
            {
                return; // not loaded yet; keep polling
            }

            RhinoApp.Idle -= onIdle; // loaded: subscribe once, then stop polling
            try
            {
                Subscribe(onChanged);
                log("grasshopper: subscribed to DocumentServer add/remove events");
                onChanged(); // reflect any definition already open at subscription time
            }
            catch (Exception ex)
            {
                log($"grasshopper: could not subscribe to DocumentServer events (definitions will refresh only on Rhino events): {ex.Message}");
            }
        };
        RhinoApp.Idle += onIdle;
    }

    internal static bool GrasshopperLoaded() =>
        AppDomain.CurrentDomain.GetAssemblies().Any(a => a.FullName is { } n && n.StartsWith("Grasshopper,", StringComparison.Ordinal));

    private static void Subscribe(Action onChanged)
    {
        if (_subscribed)
        {
            return;
        }

        var server = global::Grasshopper.Instances.DocumentServer;
        if (server is null)
        {
            return;
        }

        server.DocumentAdded += (_, _) => onChanged();
        server.DocumentRemoved += (_, _) => onChanged();
        _subscribed = true;
    }
}
