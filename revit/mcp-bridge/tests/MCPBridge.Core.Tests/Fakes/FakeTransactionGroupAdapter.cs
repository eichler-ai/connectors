using System.Collections.Generic;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Tests.Fakes;

internal sealed class FakeTransactionGroupAdapter : ITransactionGroupAdapter
{
    public string Name { get; }
    public List<string> Calls { get; } = new();

    public FakeTransactionGroupAdapter(string name)
    {
        Name = name;
    }

    public void Start() => Calls.Add("Start");

    public void Assimilate() => Calls.Add("Assimilate");

    public void RollBack() => Calls.Add("RollBack");
}
