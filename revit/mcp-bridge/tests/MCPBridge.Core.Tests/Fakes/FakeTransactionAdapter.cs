using System;
using System.Collections.Generic;
using System.Linq;
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
    public IReadOnlyList<FailureSummary> FailuresToReport { get; set; } = Array.Empty<FailureSummary>();

    private Action<IReadOnlyList<FailureSummary>>? _observer;

    public void Start() => Calls.Add("Start");

    public void SetFailuresObserver(Action<IReadOnlyList<FailureSummary>> observer) => _observer = observer;

    public TransactionCommitResult Commit()
    {
        Calls.Add("Commit");
        if (ThrowOnCommit)
        {
            throw new InvalidOperationException("simulated commit failure");
        }

        if (FailuresToReport.Count > 0)
        {
            _observer?.Invoke(FailuresToReport);
            if (FailuresToReport.Any(f => f.IsError))
            {
                return TransactionCommitResult.RolledBack;
            }
        }

        return TransactionCommitResult.Committed;
    }

    public void RollBack() => Calls.Add("RollBack");
}
