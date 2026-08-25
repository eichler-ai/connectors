using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Tests.Fakes;

public sealed class FakeUiApplicationAdapter : IUiApplicationAdapter
{
    public IUiDocumentAdapter? ActiveUiDocument { get; init; }
}

public sealed class FakeUiDocumentAdapter : IUiDocumentAdapter
{
    public IDocumentAdapter Document { get; init; } = new FakeDocumentAdapter();

    IScriptDocument IScriptUiDocument.Document => Document;
}
