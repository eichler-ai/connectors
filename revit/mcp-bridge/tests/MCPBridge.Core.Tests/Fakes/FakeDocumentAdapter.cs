using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Tests.Fakes;

/// <summary>
/// Fake behind the RevitAdapter seam (per the revit-connector-development skill's testing strategy) --
/// records calls instead of touching a live Document.
///
/// Deliberately does NOT implement IRawDocumentSource (PRD §14): it has no real
/// Autodesk.Revit.DB.Document and never can, since Document is sealed and non-constructible outside a
/// live Revit session. ScriptGlobals turns that absence into a clear, signposted error naming
/// revit/test-harness. Just as importantly, not implementing it keeps MCPBridge.Core.Tests.dll free of
/// a direct RevitAPI assembly reference -- see IRawDocumentSource's doc comment for why that matters
/// (with one, xUnit silently skips this entire assembly and `dotnet test` still exits 0).
/// </summary>
internal sealed class FakeDocumentAdapter : IDocumentAdapter
{
    public string Title { get; init; } = "FakeDocument";
    public string? PathName { get; init; }
    public bool IsWorkshared { get; init; }
    public string? CentralModelPath { get; init; }
    public string DocumentId { get; init; } = "doc-fake0000000000";
    public FakeTransactionAdapter? LastTransaction { get; private set; }
    public FakeTransactionGroupAdapter? LastTransactionGroup { get; private set; }

    public ITransactionAdapter CreateTransaction(string name)
    {
        var tx = new FakeTransactionAdapter(name);
        LastTransaction = tx;
        return tx;
    }

    public ITransactionGroupAdapter CreateTransactionGroup(string name)
    {
        var group = new FakeTransactionGroupAdapter(name);
        LastTransactionGroup = group;
        return group;
    }

    /// <summary>
    /// Always false -- this fake has no real backing Document to compare by reference, and no fake needs
    /// one: DocumentId is what tier-1 tests dedupe on. See IDocumentAdapter's own doc comment for why
    /// this member exists and stays Revit-type-free.
    /// </summary>
    public bool ReferencesSameUnderlyingDocumentAs(IDocumentAdapter other) => false;
}
