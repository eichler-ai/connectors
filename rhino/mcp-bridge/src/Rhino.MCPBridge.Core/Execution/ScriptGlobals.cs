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
    public static IReadOnlyList<string> GlobalNames { get; } = new[] { "Document", "CancellationToken", "Connector", "GrasshopperDocument" };

    public RhinoDoc Document { get; }

    public CancellationToken CancellationToken { get; }

    public Connector Connector { get; }

    /// <summary>The addressed Grasshopper definition (a <c>Grasshopper.Kernel.GH_Document</c>), or null when
    /// no <c>gh_document_id</c> was passed or Grasshopper is not loaded (PRD §10). Typed <c>object</c> on
    /// purpose: a real <c>GH_Document</c> would make this type hard-depend on Grasshopper, and since
    /// Grasshopper is demand-loaded, Roslyn could not resolve the type and EVERY C# script — even ones that
    /// never touch Grasshopper — would fail to compile. A script that wants it casts:
    /// <c>(Grasshopper.Kernel.GH_Document)GrasshopperDocument</c> (which compiles because Grasshopper IS
    /// loaded whenever this is non-null). Python's <c>ghdoc</c> gets the same object, castless.</summary>
    public object? GrasshopperDocument { get; }

    private readonly string _bridgeVersion;
    private readonly string? _runLabel;

    internal ScriptGlobals(RhinoDoc document, CancellationToken cancellationToken, string bridgeVersion, string? runLabel, object? grasshopperDocument = null)
    {
        Document = document;
        CancellationToken = cancellationToken;
        _bridgeVersion = bridgeVersion;
        _runLabel = runLabel;
        GrasshopperDocument = grasshopperDocument;
        Connector = new Connector(this);
    }

    // IConnectorRuntime -- explicit so a script sees only the three globals above.
    string IConnectorRuntime.BridgeVersion => _bridgeVersion;
    string? IConnectorRuntime.RunLabel => _runLabel;
}
