using System.Threading;
using Eichler.Connectors.Rhino;

namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// The script's scope (rhino/docs/PRD.md §06): exactly three globals, case-sensitively --
/// <c>Document</c> (the real <see cref="RhinoDoc"/> the call was routed to), <c>CancellationToken</c>
/// (cooperative cancellation, PRD §06), and <c>Connector</c> (this connector's own API, PRD §06 /
/// CONVENTIONS.md). Public because Roslyn binds a script's globals by this type; the constructor is
/// internal so a script cannot make another. The one type in Core that names a RhinoCommon type,
/// for the same reason Revit's ScriptGlobals names Autodesk's: it IS the script-facing contract.
/// </summary>
public sealed class ScriptGlobals : IConnectorRuntime
{
    /// <summary>The names a script sees, for the compile-time collision check discovery runs.</summary>
    public static IReadOnlyList<string> GlobalNames { get; } = new[] { "Document", "CancellationToken", "Connector" };

    public RhinoDoc Document { get; }

    public CancellationToken CancellationToken { get; }

    public Connector Connector { get; }

    private readonly string _bridgeVersion;
    private readonly string? _runLabel;

    internal ScriptGlobals(RhinoDoc document, CancellationToken cancellationToken, string bridgeVersion, string? runLabel)
    {
        Document = document;
        CancellationToken = cancellationToken;
        _bridgeVersion = bridgeVersion;
        _runLabel = runLabel;
        Connector = new Connector(this);
    }

    // IConnectorRuntime -- explicit so a script sees only the three globals above.
    string IConnectorRuntime.BridgeVersion => _bridgeVersion;
    string? IConnectorRuntime.RunLabel => _runLabel;
}
