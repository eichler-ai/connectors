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
/// Phase 1): IScriptDocument/IUiApplicationAdapter/IUiDocumentAdapter today
/// expose only what Phase 1's own trivial-expression scripts need (Title) --
/// nowhere near real Revit API access (element queries, geometry, etc.).
/// Because this exact globals shape is what real scripts will bind against by
/// name once discovery/execute_script grows real API surface (Phase 3+),
/// growing these interfaces later either balloons them into re-implementing
/// large parts of the Revit API (defeating the "thin seam" premise) or forces
/// a breaking change to a globals type scripts already depend on. Revisit
/// this class's shape explicitly before Phase 3 lands real API access --
/// don't let it grow ad hoc, member by member.
/// </summary>
public sealed class ScriptGlobals
{
    // Property casing here is a public, external contract (PRD §06): an agent-authored script
    // binds to these identifiers by name in its scope, so it must match the PRD's published
    // names -- Document, UIApplication, UIDocument -- exactly.
    //
    // Document/UIApplication/UIDocument are deliberately typed as IScriptDocument/IScriptUiApplication/
    // IScriptUiDocument, not IDocumentAdapter/IUiApplicationAdapter/IUiDocumentAdapter: CreateTransaction/
    // CreateTransactionGroup exist on IDocumentAdapter for TransactionScriptExecutor's own ambient
    // Transaction/TransactionGroup (which already wraps every script run), not for the script to call --
    // Revit only allows one open Transaction per Document, so a script calling CreateTransaction on the
    // same Document the executor already opened one on always fails, whether reached via `Document`,
    // `UIDocument.Document`, or `UIApplication.ActiveUiDocument.Document` -- a second independent PR
    // review found this third path still reachable after the first two were closed, so ALL THREE globals
    // are narrowed, not just the two that had already been caught. Confirmed live, not hypothetical --
    // see IScriptDocument's doc comment.
    public IScriptDocument Document { get; }
    public IScriptUiApplication UIApplication { get; }
    public IScriptUiDocument? UIDocument { get; }
    public CancellationToken CancellationToken { get; }

    public ScriptGlobals(
        IDocumentAdapter document,
        IUiApplicationAdapter uiApplication,
        IUiDocumentAdapter? uiDocument,
        CancellationToken cancellationToken)
    {
        Document = document;
        UIApplication = uiApplication;
        UIDocument = uiDocument;
        CancellationToken = cancellationToken;
    }
}
