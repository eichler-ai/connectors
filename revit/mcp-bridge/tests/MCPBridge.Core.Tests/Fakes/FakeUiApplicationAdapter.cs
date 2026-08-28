using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Tests.Fakes;

/// <summary>See FakeDocumentAdapter: deliberately does not implement IRawUiApplicationSource (PRD §14).</summary>
public sealed class FakeUiApplicationAdapter : IUiApplicationAdapter
{
    public IUiDocumentAdapter? ActiveUiDocument { get; init; }
}

/// <summary>See FakeDocumentAdapter: deliberately does not implement IRawUiDocumentSource (PRD §14).</summary>
public sealed class FakeUiDocumentAdapter : IUiDocumentAdapter
{
    public IDocumentAdapter Document { get; init; } = new FakeDocumentAdapter();
}
