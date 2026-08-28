using System;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Tests.Fakes;

/// <summary>
/// See FakeDocumentAdapter: deliberately does not implement IRawUiApplicationSource (PRD §14).
///
/// It DOES implement IDocumentCreationSource (issue #24), and that difference is the point of that
/// interface's shape: IRawUiApplicationSource returns a real Autodesk.Revit.UI.UIApplication, which a
/// fake genuinely cannot supply and which would drag a direct RevitAPI reference into this test
/// assembly; IDocumentCreationSource returns an IDocumentAdapter, so the create-and-track logic stays
/// tier-1 testable while only the final unwrap to a real Document remains tier-2.
/// </summary>
public sealed class FakeUiApplicationAdapter : IUiApplicationAdapter, IDocumentCreationSource
{
    public IUiDocumentAdapter? ActiveUiDocument { get; init; }

    /// <summary>The adapter handed back by both creation members; null means "this test never creates one".</summary>
    public IDocumentAdapter? CreatedDocument { get; init; }

    public string? LastProjectTemplatePath { get; private set; }
    public string? LastFamilyTemplatePath { get; private set; }

    public IDocumentAdapter CreateProjectDocument(string? templatePath)
    {
        LastProjectTemplatePath = templatePath;
        return CreatedDocument
            ?? throw new InvalidOperationException(
                "This FakeUiApplicationAdapter was not given a CreatedDocument, so it cannot create one.");
    }

    public IDocumentAdapter CreateFamilyDocument(string templatePath)
    {
        LastFamilyTemplatePath = templatePath;
        return CreatedDocument
            ?? throw new InvalidOperationException(
                "This FakeUiApplicationAdapter was not given a CreatedDocument, so it cannot create one.");
    }
}

/// <summary>See FakeDocumentAdapter: deliberately does not implement IRawUiDocumentSource (PRD §14).</summary>
public sealed class FakeUiDocumentAdapter : IUiDocumentAdapter
{
    public IDocumentAdapter Document { get; init; } = new FakeDocumentAdapter();
}
