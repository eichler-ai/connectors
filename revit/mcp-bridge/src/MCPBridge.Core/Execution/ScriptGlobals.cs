using System.Threading;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Execution;

/// <summary>
/// The globals object exposed to script scope (PRD §06 step 3 / "Cancellation" -
/// cooperative path). Deliberately built on the RevitAdapter interfaces, not raw
/// Revit API types, so scripts run against fakes in unit tests exercise the exact
/// same globals shape a live script would see.
///
/// Forward-compatibility risk, flagged deliberately (adversarial code review,
/// Phase 1): IDocumentAdapter/IUiApplicationAdapter/IUiDocumentAdapter today
/// expose only what Phase 1's own trivial-expression scripts need (Title,
/// transaction creation) -- nowhere near real Revit API access (element
/// queries, geometry, etc.). Because this exact globals shape is what real
/// scripts will bind against by name once discovery/execute_script grows real
/// API surface (Phase 3+), growing these interfaces later either balloons them
/// into re-implementing large parts of the Revit API (defeating the "thin
/// seam" premise) or forces a breaking change to a globals type scripts
/// already depend on. Revisit this class's shape explicitly before Phase 3
/// lands real API access -- don't let it grow ad hoc, member by member.
/// </summary>
public sealed class ScriptGlobals
{
    public IDocumentAdapter Document { get; }
    public IUiApplicationAdapter UiApplication { get; }
    public IUiDocumentAdapter? UiDocument { get; }
    public CancellationToken CancellationToken { get; }

    public ScriptGlobals(
        IDocumentAdapter document,
        IUiApplicationAdapter uiApplication,
        IUiDocumentAdapter? uiDocument,
        CancellationToken cancellationToken)
    {
        Document = document;
        UiApplication = uiApplication;
        UiDocument = uiDocument;
        CancellationToken = cancellationToken;
    }
}
