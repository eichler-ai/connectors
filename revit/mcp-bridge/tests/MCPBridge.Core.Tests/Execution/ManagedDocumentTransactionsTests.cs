using System;
using System.Collections.Generic;
using System.Linq;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Tests.Fakes;
using MCPBridge.RevitAdapter;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

/// <summary>
/// Issue #24's tier-1 surface: everything about managing N documents' transactions that does NOT need
/// a live Revit session. Creating a real document and writing to it is tier-2 by construction (see the
/// revit-connector-development skill) -- what is testable here is the decision logic that generalizes
/// TransactionScriptExecutor from one (TransactionGroup, Transaction) pair to N: open order, commit
/// order, rollback coverage, and what a partial failure across documents reports.
/// </summary>
public class ManagedDocumentTransactionsTests
{
    private const string TransactionName = "MCP Bridge Script";

    /// <summary>
    /// Records every call across ALL documents into one shared journal, which is the only way to assert
    /// on ORDER BETWEEN documents -- the per-document Calls lists on the shared fakes cannot express it.
    /// </summary>
    private sealed class JournalingDocumentAdapter : IDocumentAdapter
    {
        private readonly List<string> _journal;

        public JournalingDocumentAdapter(string title, List<string> journal)
        {
            Title = title;
            _journal = journal;
        }

        public string Title { get; }
        public string? PathName => null;
        public bool IsWorkshared => false;
        public string? CentralModelPath => null;
        public string DocumentId => "tmp-" + Title;

        public bool ThrowOnCommit { get; set; }
        public bool ThrowOnStartTransaction { get; set; }
        public bool ThrowOnAssimilate { get; set; }
        public IReadOnlyList<FailureSummary> FailuresToReport { get; set; } = Array.Empty<FailureSummary>();

        public ITransactionAdapter CreateTransaction(string name) => new JournalingTransaction(this);

        public ITransactionGroupAdapter CreateTransactionGroup(string name) => new JournalingGroup(this);

        private void Record(string call) => _journal.Add($"{Title}:{call}");

        private sealed class JournalingTransaction : ITransactionAdapter
        {
            private readonly JournalingDocumentAdapter _owner;

            public JournalingTransaction(JournalingDocumentAdapter owner) => _owner = owner;

            public IReadOnlyList<FailureSummary> CommitFailures { get; private set; } = Array.Empty<FailureSummary>();

            public void Start()
            {
                _owner.Record("tx.Start");
                if (_owner.ThrowOnStartTransaction)
                {
                    throw new InvalidOperationException($"{_owner.Title}: simulated transaction-start failure");
                }
            }

            public TransactionCommitResult Commit()
            {
                _owner.Record("tx.Commit");
                if (_owner.ThrowOnCommit)
                {
                    throw new InvalidOperationException($"{_owner.Title}: simulated commit failure");
                }

                CommitFailures = _owner.FailuresToReport;
                return CommitFailures.Any(f => f.IsError)
                    ? TransactionCommitResult.RolledBack
                    : TransactionCommitResult.Committed;
            }

            public void RollBack() => _owner.Record("tx.RollBack");
        }

        private sealed class JournalingGroup : ITransactionGroupAdapter
        {
            private readonly JournalingDocumentAdapter _owner;

            public JournalingGroup(JournalingDocumentAdapter owner) => _owner = owner;

            public void Start() => _owner.Record("group.Start");

            public void Assimilate()
            {
                _owner.Record("group.Assimilate");
                if (_owner.ThrowOnAssimilate)
                {
                    throw new InvalidOperationException($"{_owner.Title}: simulated assimilate failure");
                }
            }

            public void RollBack() => _owner.Record("group.RollBack");
        }
    }

    private static ManagedDocumentTransactions NewSet() =>
        new(TransactionName, new FakeUiApplicationAdapter());

    private static FailureSummary Error(string message) => new(isError: true, message, "err", new[] { "1" });

    [Fact]
    public void Open_StartsTheGroupBeforeTheTransaction()
    {
        var journal = new List<string>();
        var set = NewSet();

        set.Open(new JournalingDocumentAdapter("ambient", journal), isAmbient: true);

        Assert.Equal(new[] { "ambient:group.Start", "ambient:tx.Start" }, journal);
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void Open_RollsBackTheGroup_WhenStartingTheTransactionThrows()
    {
        // Otherwise the group is started, untracked, and never closed -- an open TransactionGroup
        // leaked into the live session with nothing left holding a reference to it.
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("ambient", journal) { ThrowOnStartTransaction = true };

        Assert.Throws<InvalidOperationException>(() => set.Open(document));

        Assert.Equal(new[] { "ambient:group.Start", "ambient:tx.Start", "ambient:group.RollBack" }, journal);
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void CommitAll_CommitsCreatedDocumentsFirstAndTheAmbientDocumentLast()
    {
        // The ordering is the whole partial-failure defence (see ManagedDocumentTransactions' doc
        // comment): with the ambient document committed last, a failure among the created ones can
        // still be answered by rolling the human's real document back.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("ambient", journal), isAmbient: true);
        set.Open(new JournalingDocumentAdapter("created-1", journal));
        set.Open(new JournalingDocumentAdapter("created-2", journal));

        var result = set.CommitAll();

        Assert.True(result.Success);
        Assert.Equal(
            new[]
            {
                "created-1:tx.Commit", "created-1:group.Assimilate",
                "created-2:tx.Commit", "created-2:group.Assimilate",
                "ambient:tx.Commit", "ambient:group.Assimilate",
            },
            journal.Where(c => c.Contains("Commit") || c.Contains("Assimilate")).ToArray());
        Assert.Equal(new[] { "created-1", "created-2", "ambient (active document)" }, result.CommittedDocuments);
        Assert.Empty(result.RolledBackDocuments);
        Assert.False(result.IsPartial);
    }

    [Fact]
    public void CommitAll_AggregatesFailuresAcrossEveryDocument()
    {
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new JournalingDocumentAdapter("ambient", journal)
        {
            FailuresToReport = new[] { new FailureSummary(isError: false, "ambient warning", "w1", Array.Empty<string>()) },
        };
        var created = new JournalingDocumentAdapter("created", journal)
        {
            FailuresToReport = new[] { new FailureSummary(isError: false, "created warning", "w2", Array.Empty<string>()) },
        };
        set.Open(ambient, isAmbient: true);
        set.Open(created);

        var result = set.CommitAll();

        Assert.True(result.Success);
        Assert.Equal(new[] { "created warning", "ambient warning" }, result.CommitFailures.Select(f => f.Message));
    }

    [Fact]
    public void RollBackAll_RollsBackEveryDocument_MostRecentlyOpenedFirst()
    {
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("ambient", journal), isAmbient: true);
        set.Open(new JournalingDocumentAdapter("created", journal));
        journal.Clear();

        set.RollBackAll();

        Assert.Equal(
            new[] { "created:tx.RollBack", "created:group.RollBack", "ambient:tx.RollBack", "ambient:group.RollBack" },
            journal);
    }

    [Fact]
    public void CommitAll_RollsBackTheFailingDocumentAndEveryUnattemptedOne_WhenTheFirstCommitThrows()
    {
        // Nothing committed, so nothing is partial -- this is the ordinary total-failure case and it
        // must keep behaving exactly like the pre-issue-#24 single-document path did.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("ambient", journal), isAmbient: true);
        set.Open(new JournalingDocumentAdapter("created", journal) { ThrowOnCommit = true });
        journal.Clear();

        var result = set.CommitAll();

        Assert.False(result.Success);
        Assert.False(result.IsPartial);
        Assert.Empty(result.CommittedDocuments);
        Assert.Equal(new[] { "created", "ambient (active document)" }, result.RolledBackDocuments);
        Assert.Equal(
            new[]
            {
                "created:tx.Commit", "created:tx.RollBack", "created:group.RollBack",
                "ambient:tx.RollBack", "ambient:group.RollBack",
            },
            journal);
    }

    [Fact]
    public void CommitAll_ReportsPartial_WhenALaterCommitFailsAfterAnEarlierOneSucceeded()
    {
        // THE CASE ISSUE #24 SINGLED OUT AS UNDETERMINED. Revit cannot un-commit a committed
        // Transaction, so created-1's changes genuinely survive. The design answer is to confine the
        // damage by ordering (the ambient document is still rolled back) and to REPORT it rather than
        // paper over it -- PRD §01.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("ambient", journal), isAmbient: true);
        set.Open(new JournalingDocumentAdapter("created-1", journal));
        set.Open(new JournalingDocumentAdapter("created-2", journal) { ThrowOnCommit = true });

        var result = set.CommitAll();

        Assert.False(result.Success);
        Assert.True(result.IsPartial);
        Assert.Equal(new[] { "created-1" }, result.CommittedDocuments);
        Assert.Equal(new[] { "created-2", "ambient (active document)" }, result.RolledBackDocuments);
        Assert.Contains("ambient:group.RollBack", journal);
        Assert.DoesNotContain("ambient:group.Assimilate", journal);
    }

    [Fact]
    public void CommitAll_DoesNotRollBackTheTransactionItself_WhenRevitAlreadyRolledItBack()
    {
        // TransactionCommitResult.RolledBack means Revit's own Failures API already closed the
        // Transaction (ProceedWithRollBack); calling RollBack() on it again would be invalid. Only the
        // group needs closing. This is the single-document rule, preserved per-document.
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new JournalingDocumentAdapter("ambient", journal)
        {
            FailuresToReport = new[] { Error("a hard failure") },
        };
        set.Open(ambient, isAmbient: true);
        journal.Clear();

        var result = set.CommitAll();

        Assert.False(result.Success);
        Assert.Equal(new[] { "ambient:tx.Commit", "ambient:group.RollBack" }, journal);
        Assert.Equal("a hard failure", result.Failure!.Message);
        Assert.Single(result.CommitFailures);
    }

    [Fact]
    public void CommitAll_ReportsFailure_WhenAssimilateThrowsAfterAGoodCommit()
    {
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("ambient", journal) { ThrowOnAssimilate = true }, isAmbient: true);
        journal.Clear();

        var result = set.CommitAll();

        Assert.False(result.Success);
        Assert.Equal(new[] { "ambient:tx.Commit", "ambient:group.Assimilate", "ambient:group.RollBack" }, journal);
        Assert.Equal(new[] { "ambient (active document)" }, result.RolledBackDocuments);
    }

    [Fact]
    public void CreateAndOpenProjectDocument_CreatesTheDocumentAndOpensItsTransaction()
    {
        var journal = new List<string>();
        var created = new JournalingDocumentAdapter("created", journal);
        var set = new ManagedDocumentTransactions(
            TransactionName,
            new FakeUiApplicationAdapter { CreatedDocument = created });

        var returned = set.CreateAndOpenProjectDocument(templatePath: null);

        Assert.Same(created, returned);
        Assert.Equal(1, set.Count);
        Assert.Equal(new[] { "created:group.Start", "created:tx.Start" }, journal);
    }

    [Fact]
    public void CreateAndOpenFamilyDocument_CreatesTheDocumentAndOpensItsTransaction()
    {
        var journal = new List<string>();
        var created = new JournalingDocumentAdapter("created-family", journal);
        var uiApplication = new FakeUiApplicationAdapter { CreatedDocument = created };
        var set = new ManagedDocumentTransactions(TransactionName, uiApplication);

        var returned = set.CreateAndOpenFamilyDocument("C:/templates/Metric Generic Model.rft");

        Assert.Same(created, returned);
        Assert.Equal("C:/templates/Metric Generic Model.rft", uiApplication.LastFamilyTemplatePath);
        Assert.Equal(new[] { "created-family:group.Start", "created-family:tx.Start" }, journal);
    }

    [Fact]
    public void CreateAndOpenProjectDocument_FailsWithASignpostedMessage_WhenTheAdapterCannotCreateDocuments()
    {
        // A fake with no IDocumentCreationSource is exactly the tier-1 case; the message has to say
        // where such a test belongs rather than surfacing an opaque cast failure (PRD §01).
        var set = new ManagedDocumentTransactions(TransactionName, new NonCreatingUiApplicationAdapter());

        var ex = Assert.Throws<NotSupportedException>(() => set.CreateAndOpenProjectDocument(null));

        Assert.Contains("IDocumentCreationSource", ex.Message);
        Assert.Contains("revit/test-harness", ex.Message);
    }

    private sealed class NonCreatingUiApplicationAdapter : IUiApplicationAdapter
    {
        public IUiDocumentAdapter? ActiveUiDocument => null;
    }
}
