namespace Eichler.Connectors.Rhino;

/// <summary>
/// What <see cref="Connector"/> forwards to. Internal on purpose: a script can name
/// <c>Connector</c> but never this, so the capability lives on the bridge's side of the seam
/// (the Revit connector's "public means script-reachable" rule). Implemented by the bridge's
/// ScriptGlobals; grows a member per phase.
/// </summary>
internal interface IConnectorRuntime
{
    /// <summary>The bridge's version string, for a script that wants to know what it is running under.</summary>
    string BridgeVersion { get; }

    /// <summary>The label the caller gave this run (the `label` parameter), or null.</summary>
    string? RunLabel { get; }
}
