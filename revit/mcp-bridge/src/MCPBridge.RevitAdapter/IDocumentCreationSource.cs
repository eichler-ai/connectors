namespace MCPBridge.RevitAdapter;

/// <summary>
/// Creates brand-new in-memory Revit documents (issue #24) -- the adapter half of
/// ScriptGlobals.CreateProjectDocument/CreateFamilyDocument, which hand a script a document it can
/// actually WRITE to because the executor opens and manages a Transaction/TransactionGroup for it in
/// the same step (see MCPBridge.Core.Execution.ManagedDocumentTransactions).
///
/// WHY THIS IS A CAPABILITY INTERFACE, like IRawDocumentSource: it is implemented only by the real
/// RevitUiApplicationAdapter, because creating a document means calling
/// Autodesk.Revit.ApplicationServices.Application.NewProjectDocument/NewFamilyDocument, which needs a
/// live Revit session. ScriptGlobals type-tests for it and produces a signposted error when an adapter
/// does not implement it, rather than returning null (PRD §01).
///
/// WHY IT NAMES NO REVIT TYPE, unlike IRawDocumentSource, and why that is load-bearing rather than
/// incidental: returning <see cref="IDocumentAdapter"/> (not Autodesk.Revit.DB.Document) means a
/// MCPBridge.Core.Tests fake CAN implement this without giving that test assembly its own direct
/// RevitAPI reference -- the thing that once made xUnit silently skip the entire assembly while
/// `dotnet test` still exited 0 (see IRawDocumentSource's doc comment). That is what keeps the
/// create-and-track decision logic tier-1 testable at all; only the final unwrap to the real Document,
/// which a script needs and a fake genuinely cannot supply, stays tier-2.
///
/// The returned adapter is expected to ALSO implement IRawDocumentSource (RevitDocumentAdapter does),
/// since ScriptGlobals has to hand the real Document back to the script.
/// </summary>
public interface IDocumentCreationSource
{
    /// <summary>
    /// Creates a new project document from <paramref name="templatePath"/>, or from the Revit
    /// install's own DefaultProjectTemplate when that is null/empty -- the fixture-system case (PRD
    /// §13), where the point is a blank document with no template asset committed to this repo.
    /// Throws when neither a template path nor a usable default is available, rather than guessing.
    /// </summary>
    IDocumentAdapter CreateProjectDocument(string? templatePath);

    /// <summary>
    /// Creates a new family document from <paramref name="templatePath"/>. Unlike a project document
    /// there is no install-wide default family template to fall back on -- the caller must name one.
    /// </summary>
    IDocumentAdapter CreateFamilyDocument(string templatePath);
}
