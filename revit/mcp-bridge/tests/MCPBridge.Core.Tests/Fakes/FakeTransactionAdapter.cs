using System.Collections.Generic;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Tests.Fakes;

public sealed class FakeTransactionAdapter : ITransactionAdapter
{
    public string Name { get; }
    public List<string> Calls { get; } = new();

    public FakeTransactionAdapter(string name)
    {
        Name = name;
    }

    public bool ThrowOnCommit { get; set; }

    public void Start() => Calls.Add("Start");

    public void Commit()
    {
        Calls.Add("Commit");
        if (ThrowOnCommit)
        {
            throw new System.InvalidOperationException("simulated commit failure");
        }
    }

    public void RollBack() => Calls.Add("RollBack");
}
