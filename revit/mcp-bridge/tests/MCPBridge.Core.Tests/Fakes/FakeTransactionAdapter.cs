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

    public IReadOnlyList<FailureSummary> CommitFailures { get; private set; } = Array.Empty<FailureSummary>();

    public void Start() => Calls.Add("Start");

    public TransactionCommitResult Commit()
    {
        Calls.Add("Commit");
        if (ThrowOnCommit)
        {
            throw new InvalidOperationException("simulated commit failure");
        }

        CommitFailures = FailuresToReport;
        return FailuresToReport.Any(f => f.IsError) ? TransactionCommitResult.RolledBack : TransactionCommitResult.Committed;
    }

    public void RollBack() => Calls.Add("RollBack");
}
