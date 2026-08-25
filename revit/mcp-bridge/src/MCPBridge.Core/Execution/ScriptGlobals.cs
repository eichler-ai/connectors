using System.Threading;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Execution;

/// <summary>
/// The globals object exposed to script scope (PRD §06 step 3 / "Cancellation" -
/// cooperative path). Deliberately built on the RevitAdapter interfaces, not raw
/// Revit API types, so scripts run against fakes in unit tests exercise the exact
/// same globals shape a live script would see.
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
