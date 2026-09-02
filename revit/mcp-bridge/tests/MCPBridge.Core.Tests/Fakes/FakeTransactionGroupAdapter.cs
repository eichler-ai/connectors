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

    public string? LastName { get; private set; }

    public bool ThrowOnSetName { get; set; }

    public void SetName(string name)
    {
        Calls.Add("SetName");
        if (ThrowOnSetName)
        {
            throw new InvalidOperationException("simulated SetName refusal");
        }

        LastName = name;
    }

    /// <summary>See FakeTransactionAdapter.ThrowOnDispose.</summary>
    public bool ThrowOnDispose { get; set; }

    public void Dispose()
    {
        Calls.Add("Dispose");
        if (ThrowOnDispose)
        {
            throw new InvalidOperationException("simulated dispose failure");
        }
    }
}
