namespace MCPBridge.RevitAdapter;

/// <summary>
/// Wraps an already-open Autodesk.Revit.DB.Document (found via Application.Documents, not created this
/// execute_script call) in a fresh IDocumentAdapter, for ManagedDocumentTransactions.OpenExisting to open
/// a managed transaction on -- the adapter half of ScriptGlobals.OpenForWriting, symmetric to
/// IDocumentCreationSource's CreateProjectDocument/CreateFamilyDocument for a document that already exists.
///
/// A SEPARATE interface from IDocumentCreationSource, deliberately, not one more method added to it.
/// IDocumentCreationSource's own doc comment explains it "names no Revit type" specifically so
/// MCPBridge.Core.Tests fakes implementing it never need a RevitAPI reference (the exact class of bug that
/// once made a whole test assembly silently unloadable outside Revit while `dotnet test` still exited 0).
/// Adding a method to an interface forces every existing implementer -- including those tier-1 fakes -- to
/// implement it too, and this method's parameter is unavoidably Revit-typed (Autodesk.Revit.DB.Document is
/// exactly what the script already found and is handing back in). A brand-new interface avoids that: no
/// fake anywhere needs to implement THIS one, since tier-1 tests exercise ManagedDocumentTransactions'
/// double-open guard and commit-ordering directly through Open(IDocumentAdapter, bool) -- already
/// interface-typed and fake-compatible -- without ever going through raw-Document wrapping at all.
///
/// Implemented only by RevitUiApplicationAdapter, the real live adapter -- same reason
/// IDocumentCreationSource is: wrapping a Document needs nothing beyond RevitDocumentAdapter's existing
/// constructor, but that constructor lives in this same internal, script-unreachable assembly, so only
/// real production code (never a script) can call it. See IDocumentAdapter's own doc comment for the
/// "public means script-reachable" rule this interface, like its siblings, is deliberately internal under.
/// </summary>
internal interface IExistingDocumentSource
{
    IDocumentAdapter WrapExisting(Autodesk.Revit.DB.Document document);
}
