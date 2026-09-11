namespace Eichler.Connectors.Rhino;

/// <summary>
/// This connector's own additions to the Rhino API, reached from a script as the <c>Connector</c>
/// global (C#) or <c>connector</c> (Python). Everything here is the connector's, not Rhino's: it is
/// indexed by discovery under <c>Eichler.Connectors.Rhino</c> so the provenance is visible beside
/// <c>Rhino.*</c> members. Members land phase by phase (rhino/docs/PRD.md §06): file exchange
/// (<c>Publish</c>, <c>ImportsDirectory</c>, <c>ExportsDirectory</c>), <c>CaptureView</c>,
/// <c>Grasshopper</c>, <c>Settle</c>.
/// </summary>
public sealed class Connector
{
    private readonly IConnectorRuntime _runtime;

    internal Connector(IConnectorRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>The version of the Rhino MCP Bridge this script is running under, as the Status
    /// command and <c>list_instances</c> report it.</summary>
    public string BridgeVersion => _runtime.BridgeVersion;

    /// <summary>The <c>label</c> the caller gave this run, or null when none was given. The same
    /// text names the run's entry in Rhino's Undo history.</summary>
    public string? RunLabel => _runtime.RunLabel;

    /// <summary>PRD §08: the place to register how a modal dialog should be answered, for when a
    /// suppressible pre-show hook exists. v1 has none — RhinoCommon exposes no pre-show dialog event
    /// (§08, §17 item 4) — so this is a documented NO-OP: overrides are accepted so the API surface is
    /// stable, but nothing consults them yet. Today the dialog defences are prevention (the
    /// interactive-getter denylist, §08) and the §08 timeout-fallback window inventory (diagnosis only).</summary>
    public DialogResultOverrides DialogResultOverrides { get; } = new();
}

/// <summary>
/// PRD §08 landing spot for dialog-result overrides. A NO-OP in v1 (no suppressible hook exists yet);
/// registrations are accepted and ignored so scripts written against this surface keep working once a
/// pre-show hook lands. Capability-free — plain strings only, exposing nothing an untrusted script could
/// act on beyond a no-op.
/// </summary>
public sealed class DialogResultOverrides
{
    /// <summary>Registers the answer (a button label or result) to give a dialog identified by its title
    /// or window class. NO-OP until §08 gains a suppressible pre-show hook.</summary>
    public void Set(string dialog, string result)
    {
        // Intentionally no-op (§08): there is no pre-show dialog hook to consult these overrides yet.
    }
}
