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
}
