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

    public bool ThrowOnAssimilate { get; set; }

    public bool ThrowOnRollBack { get; set; }

    /// <summary>
    /// Runs at the start of Assimilate/RollBack -- the group's terminal step, which the executor reaches
    /// INSIDE its try block after the script has run and before the finally tears down per-run state.
    /// Since #146 Phase 3 this is the one hook a tier-1 test has for observing that state while it is
    /// still live (a read-only script opens no transaction, so a commit hook never fires).
    /// </summary>
    public Action? OnTerminal { get; set; }

    public void Assimilate()
    {
        OnTerminal?.Invoke();
        Calls.Add("Assimilate");
        if (ThrowOnAssimilate)
        {
            throw new InvalidOperationException("simulated assimilate failure");
        }
    }

    public void RollBack()
    {
        OnTerminal?.Invoke();
        Calls.Add("RollBack");
        if (ThrowOnRollBack)
        {
            throw new InvalidOperationException("simulated group-rollback failure");
        }
    }

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
