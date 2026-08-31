using System;
using System.Collections.Generic;
using System.Linq;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Tests.Fakes;

internal sealed class FakeTransactionAdapter : ITransactionAdapter
{
    public string Name { get; }
    public List<string> Calls { get; } = new();

    public FakeTransactionAdapter(string name)
    {
        Name = name;
    }

    public bool ThrowOnCommit { get; set; }

    /// <summary>
    /// Makes the best-effort unwind after a failed commit itself fail -- the "state unknown" case that
    /// makes TransactionScriptExecutor emit its partial-commit notice even when nothing committed.
    /// </summary>
    public bool ThrowOnRollBack { get; set; }
    public IReadOnlyList<FailureSummary> FailuresToReport { get; set; } = Array.Empty<FailureSummary>();

    public IReadOnlyList<FailureSummary> CommitFailures { get; private set; } = Array.Empty<FailureSummary>();

    /// <summary>
    /// Runs at the start of <see cref="Commit"/>, which the executor calls INSIDE its try block -- before
    /// the finally that tears down per-run ambient state. That makes it the one hook a tier-1 test has for
    /// observing that state while it is still live. Null by default; nothing pays for it.
    /// </summary>
    public Action? OnCommit { get; set; }

    public void Start() => Calls.Add("Start");

    public TransactionCommitResult Commit()
    {
        OnCommit?.Invoke();
        Calls.Add("Commit");
        if (ThrowOnCommit)
        {
            throw new InvalidOperationException("simulated commit failure");
        }

        CommitFailures = FailuresToReport;
        return FailuresToReport.Any(f => f.IsError) ? TransactionCommitResult.RolledBack : TransactionCommitResult.Committed;
    }

    public void RollBack()
    {
        Calls.Add("RollBack");
        if (ThrowOnRollBack)
        {
            throw new InvalidOperationException("simulated rollback failure");
        }
    }

    /// <summary>Makes Dispose throw -- pins issue #34's contract that a dispose failure never masks the original outcome nor stops later entries' handling.</summary>
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
