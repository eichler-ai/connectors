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

        /// <summary>
        /// Makes this document's own ROLLBACK fail -- the case that used to be reported as a clean
        /// rollback anyway (independent PR review finding). Split in two because the two halves unwind
        /// separately and a document is only honestly "rolled back" when BOTH succeed.
        /// </summary>
        public bool ThrowOnTransactionRollBack { get; set; }

        public bool ThrowOnGroupRollBack { get; set; }

        /// <summary>
        /// Makes the CommitFailures getter itself throw. It is an ordinary adapter property backed by a
        /// call into Revit, so it can fail like any other -- and it used to be read outside every
        /// try/catch in CommitAll, which broke that method's documented "never throws" contract.
        /// </summary>
        public bool ThrowOnReadingCommitFailures { get; set; }

        public IReadOnlyList<FailureSummary> FailuresToReport { get; set; } = Array.Empty<FailureSummary>();

        public ITransactionAdapter CreateTransaction(string name) => new JournalingTransaction(this);

        public ITransactionGroupAdapter CreateTransactionGroup(string name) => new JournalingGroup(this);

        private void Record(string call) => _journal.Add($"{Title}:{call}");

        private sealed class JournalingTransaction : ITransactionAdapter
        {
            private readonly JournalingDocumentAdapter _owner;

            public JournalingTransaction(JournalingDocumentAdapter owner) => _owner = owner;

            private IReadOnlyList<FailureSummary> _commitFailures = Array.Empty<FailureSummary>();

            public IReadOnlyList<FailureSummary> CommitFailures =>
                _owner.ThrowOnReadingCommitFailures
                    ? throw new InvalidOperationException($"{_owner.Title}: simulated CommitFailures read failure")
                    : _commitFailures;

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

                _commitFailures = _owner.FailuresToReport;
                return _commitFailures.Any(f => f.IsError)
                    ? TransactionCommitResult.RolledBack
                    : TransactionCommitResult.Committed;
            }

            public void RollBack()
            {
                _owner.Record("tx.RollBack");
                if (_owner.ThrowOnTransactionRollBack)
                {
                    throw new InvalidOperationException($"{_owner.Title}: simulated transaction-rollback failure");
                }
            }

            public void Dispose() => _owner.Record("tx.Dispose");
        }

        private sealed class JournalingGroup : ITransactionGroupAdapter
        {
            private readonly JournalingDocumentAdapter _owner;

            public JournalingGroup(JournalingDocumentAdapter owner) => _owner = owner;

            public void Dispose() => _owner.Record("group.Dispose");

            public void Start() => _owner.Record("group.Start");

            public void Assimilate()
            {
                _owner.Record("group.Assimilate");
                if (_owner.ThrowOnAssimilate)
                {
                    throw new InvalidOperationException($"{_owner.Title}: simulated assimilate failure");
                }
            }

            public void RollBack()
            {
                _owner.Record("group.RollBack");
                if (_owner.ThrowOnGroupRollBack)
                {
                    throw new InvalidOperationException($"{_owner.Title}: simulated group-rollback failure");
                }
            }
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

        Assert.Equal(new[] { "ambient:group.Start", "ambient:tx.Start", "ambient:group.RollBack", "ambient:tx.Dispose", "ambient:group.Dispose" }, journal);
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void Open_ThrowsWhenCalledTwiceForTheSameDocumentId()
    {
        // OpenForWriting (ScriptGlobals) specifically targets a document that may already be tracked --
        // the ambient one, or one opened earlier this run -- a real, script-triggerable hazard
        // CreateProjectDocument/CreateFamilyDocument never had (they only ever hand back a document that
        // didn't exist until that call returned). Comparison is by DocumentId, never ReferenceEquals, per
        // this project's own standing gotcha that Revit hands back different wrapper objects for "the
        // same" document depending on API entry point -- JournalingDocumentAdapter's DocumentId is
        // derived from Title ("tmp-" + Title), so two adapters sharing a Title collide exactly as two
        // separate RevitDocumentAdapter wrappers around the same live Document would.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("ambient", journal), isAmbient: true);

        var duplicate = new JournalingDocumentAdapter("ambient", journal);
        var ex = Assert.Throws<InvalidOperationException>(() => set.Open(duplicate));

        Assert.Contains("already open", ex.Message);
        // Independent PR review finding: this used to assert Assert.Contains("ambient", ex.Message),
        // which passes even on boilerplate text ("...already opened via CreateProjectDocument/
        // CreateFamilyDocument/OpenForWriting...") that names "ambient" as a documented CASE, not the
        // document this guard actually named. Assert on the real SafeDescribe output instead, so this
        // test genuinely fails if the guard ever names the wrong document.
        Assert.Contains("ambient (active document)", ex.Message);
        Assert.Equal(1, set.Count); // the duplicate must not have been added alongside the original.
        // No TransactionGroup/Transaction was started for the rejected duplicate -- the guard fires
        // before any Revit-side allocation, not after. Journal still holds exactly the first Open's two
        // entries; a second group.Start/tx.Start pair would mean the duplicate got through.
        Assert.Equal(new[] { "ambient:group.Start", "ambient:tx.Start" }, journal);
    }

    [Fact]
    public void Open_AllowsTwoDifferentDocuments_NoFalsePositiveOnTheGuard()
    {
        // Sanity check the new DocumentId guard doesn't reject legitimately different documents --
        // distinct titles mean distinct DocumentIds here (JournalingDocumentAdapter's own convention).
        var journal = new List<string>();
        var set = NewSet();

        set.Open(new JournalingDocumentAdapter("ambient", journal), isAmbient: true);
        set.Open(new JournalingDocumentAdapter("created", journal));

        Assert.Equal(2, set.Count);
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

    // ---------------------------------------------------------------------------------------------
    // Independent PR review (2nd round): DocumentOrigin.AdoptedExisting had ZERO tier-1 coverage --
    // not the 3-tier commit ordering, not Describe()'s "(adopted via OpenForWriting)" branch, not the
    // AnyCommittedDocumentMayBeReal true branch. All three below use OpenAdoptedForTesting, the
    // internal test-only entry point added for exactly this gap.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void CommitAll_CommitsCreatedDocumentsThenAdoptedThenAmbientLast()
    {
        // The three-tier ordering this class' own doc comment describes: CreatedThisRun (safest,
        // unsaved/in-memory) first, AdoptedExisting (may be a real saved model OpenForWriting adopted)
        // next, Ambient always last -- collapsing AdoptedExisting into the CreatedThisRun bucket would
        // let a real adopted document commit alongside genuinely-throwaway ones, exactly the ordering
        // mistake DocumentOrigin's own doc comment exists to prevent.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("ambient", journal), isAmbient: true);
        set.Open(new JournalingDocumentAdapter("created", journal));
        set.OpenAdoptedForTesting(new JournalingDocumentAdapter("adopted", journal));

        var result = set.CommitAll();

        Assert.True(result.Success);
        Assert.Equal(
            new[] { "created", "adopted (adopted via OpenForWriting)", "ambient (active document)" },
            result.CommittedDocuments);
    }

    [Fact]
    public void Entry_Describe_NamesAnAdoptedDocumentDistinctlyFromACreatedOne()
    {
        var journal = new List<string>();
        var set = NewSet();
        set.OpenAdoptedForTesting(new JournalingDocumentAdapter("adopted", journal));

        var result = set.CommitAll();

        Assert.Equal(new[] { "adopted (adopted via OpenForWriting)" }, result.CommittedDocuments);
    }

    [Fact]
    public void CommitAll_SetsAnyCommittedDocumentMayBeReal_WhenAnAdoptedDocumentCommits()
    {
        // The whole point of the flag (see ManagedDocumentCommitResult's own doc comment): an adopted
        // document may be a real, saved model, unlike a CreatedThisRun one -- this is the true branch
        // that was previously unreachable at tier 1, since AdoptedExisting could only be constructed
        // through the Revit-typed OpenExisting.
        var journal = new List<string>();
        var set = NewSet();
        set.OpenAdoptedForTesting(new JournalingDocumentAdapter("adopted", journal));

        var result = set.CommitAll();

        Assert.True(result.AnyCommittedDocumentMayBeReal);
    }

    [Fact]
    public void CommitAll_DoesNotSetAnyCommittedDocumentMayBeReal_WhenOnlyCreatedDocumentsCommit()
    {
        // The false branch, for contrast -- a run with only CreatedThisRun documents (the pre-OpenForWriting
        // shape) must not trip the flag.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("created", journal));

        var result = set.CommitAll();

        Assert.False(result.AnyCommittedDocumentMayBeReal);
    }

    [Fact]
    public void RollBackAll_RollsBackAnAdoptedDocument_SameAsAnyOther()
    {
        // OpenForWriting's headline safety guarantee: a thrown script rolls back an adopted document's
        // writes exactly like any other managed document. Nothing about AdoptedExisting changes
        // RollBackAll's behavior -- this pins that explicitly rather than leaving it to be re-derived
        // from the fact that RollBackAll doesn't branch on Origin at all.
        var journal = new List<string>();
        var set = NewSet();
        set.OpenAdoptedForTesting(new JournalingDocumentAdapter("adopted", journal));
        journal.Clear();

        set.RollBackAll();

        Assert.Equal(new[] { "adopted:tx.RollBack", "adopted:group.RollBack", "adopted:tx.Dispose", "adopted:group.Dispose" }, journal);
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void CommitAll_ADisposeFailure_NeverMasksTheOutcome_NorStopsLaterEntriesDisposal()
    {
        // Issue #34's isolation contract: disposal is post-terminal housekeeping, so a throwing
        // Dispose must change NOTHING an agent sees -- the commit result stays successful, and the
        // later entry's own disposal still runs (SafeDispose guards each half independently).
        var set = NewSet();
        var first = new FakeDocumentAdapter { DocumentId = "doc-first0000000000" };
        var second = new FakeDocumentAdapter { DocumentId = "doc-second000000000" };
        set.Open(first);
        set.Open(second, isAmbient: true);
        ((FakeTransactionAdapter)first.LastTransaction!).ThrowOnDispose = true;
        ((FakeTransactionGroupAdapter)first.LastTransactionGroup!).ThrowOnDispose = true;

        var result = set.CommitAll();

        Assert.True(result.Success);
        Assert.Contains("Dispose", ((FakeTransactionAdapter)first.LastTransaction!).Calls);
        // The first entry's GROUP is still disposed even though its transaction's dispose threw --
        // SafeDispose guards each half independently (PR review: without this line, collapsing its
        // two try blocks into one would pass every test).
        Assert.Contains("Dispose", ((FakeTransactionGroupAdapter)first.LastTransactionGroup!).Calls);
        Assert.Contains("Dispose", ((FakeTransactionAdapter)second.LastTransaction!).Calls);
        Assert.Contains("Dispose", ((FakeTransactionGroupAdapter)second.LastTransactionGroup!).Calls);
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
            new[]
            {
                "created:tx.RollBack", "created:group.RollBack", "created:tx.Dispose", "created:group.Dispose",
                "ambient:tx.RollBack", "ambient:group.RollBack", "ambient:tx.Dispose", "ambient:group.Dispose",
            },
            journal);
    }

    [Fact]
    public void RollBackAll_IsIdempotent_SoTheExecutorCanCallItUnconditionallyFromFinally()
    {
        // Self-review finding: TransactionScriptExecutor now calls RollBackAll() from its `finally` as
        // a safety net for the case where the runner THROWS rather than returning a failed outcome --
        // no branch would otherwise close anything. That only works if a second call is a no-op, so
        // this pins it rather than leaving it to be re-derived from the entry-clearing implementation.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("ambient", journal), isAmbient: true);
        set.RollBackAll();
        journal.Clear();

        set.RollBackAll();

        Assert.Empty(journal);
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void RollBackAll_AfterCommitAll_DoesNothing()
    {
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("ambient", journal), isAmbient: true);
        set.Open(new JournalingDocumentAdapter("created", journal));
        set.CommitAll();
        journal.Clear();

        set.RollBackAll();

        // Rolling a committed document back from the `finally` net would undo the very work the run
        // just reported as successful -- the one thing this net must never do.
        Assert.Empty(journal);
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
                "created:tx.Commit", "created:tx.RollBack", "created:group.RollBack", "created:tx.Dispose", "created:group.Dispose",
                "ambient:tx.RollBack", "ambient:group.RollBack", "ambient:tx.Dispose", "ambient:group.Dispose",
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
        Assert.Equal(new[] { "ambient:tx.Commit", "ambient:group.RollBack", "ambient:tx.Dispose", "ambient:group.Dispose" }, journal);
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
        Assert.Equal(new[] { "ambient:tx.Commit", "ambient:group.Assimilate", "ambient:group.RollBack", "ambient:tx.Dispose", "ambient:group.Dispose" }, journal);
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

    // ---------------------------------------------------------------------------------------------
    // Independent PR review, finding 1: a rollback that itself failed was still reported as a clean
    // rollback. Every case below is about the connector telling the truth about what it does NOT know.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void CommitAll_DoesNotClaimADocumentRolledBack_WhenItsOwnRollbackThrew()
    {
        // THE BUG THIS PINS: the unwind loop added every remaining document to the "rolled back" list
        // unconditionally, with SafeRollBack swallowing the exception, so a document whose rollback
        // actually failed was reported as cleanly undone. Here that document is the AMBIENT one -- a
        // human's real open model, and precisely the document the commit ORDERING exists to protect.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(
            new JournalingDocumentAdapter("ambient", journal) { ThrowOnTransactionRollBack = true },
            isAmbient: true);
        set.Open(new JournalingDocumentAdapter("created", journal) { ThrowOnCommit = true });

        var result = set.CommitAll();

        Assert.False(result.Success);
        Assert.DoesNotContain("ambient (active document)", result.RolledBackDocuments);
        Assert.Equal(new[] { "ambient (active document)" }, result.UnknownStateDocuments);
        // The failing document's OWN rollback worked, so it is still reported honestly as rolled back.
        Assert.Equal(new[] { "created" }, result.RolledBackDocuments);
        // Best-effort is preserved: a throwing transaction rollback must not stop the group's.
        Assert.Contains("ambient:group.RollBack", journal);
    }

    [Fact]
    public void CommitAll_ReportsUnknownState_WhenOnlyTheGroupRollbackThrows()
    {
        // A document is honestly "rolled back" only when BOTH halves came back clean; the group half
        // failing on its own is just as unknown as the transaction half failing.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("ambient", journal) { ThrowOnGroupRollBack = true }, isAmbient: true);
        set.Open(new JournalingDocumentAdapter("created", journal) { ThrowOnCommit = true });

        var result = set.CommitAll();

        Assert.Equal(new[] { "ambient (active document)" }, result.UnknownStateDocuments);
        Assert.Equal(new[] { "created" }, result.RolledBackDocuments);
    }

    [Fact]
    public void CommitAll_ReportsUnknownState_WhenTheFailingDocumentsOwnRollbackThrows()
    {
        // The other half of the same bug: the document that failed to commit is unwound inside the
        // commit attempt itself, which discarded its rollback outcome just as the unwind loop did.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(
            new JournalingDocumentAdapter("ambient", journal)
            {
                ThrowOnCommit = true,
                ThrowOnTransactionRollBack = true,
            },
            isAmbient: true);

        var result = set.CommitAll();

        Assert.False(result.Success);
        Assert.Empty(result.RolledBackDocuments);
        Assert.Equal(new[] { "ambient (active document)" }, result.UnknownStateDocuments);
    }

    [Fact]
    public void CommitAll_IsPartial_WhenADocumentIsLeftInAnUnknownState_EvenThoughNothingCommitted()
    {
        // IsPartial is what makes TransactionScriptExecutor emit the script-partial-commit notice. An
        // unknown-state document with zero commits used to fall through every list and produce NO
        // notice at all -- the silence PRD §01 exists to forbid, in the case that most needs a voice.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(
            new JournalingDocumentAdapter("ambient", journal)
            {
                ThrowOnCommit = true,
                ThrowOnGroupRollBack = true,
            },
            isAmbient: true);

        var result = set.CommitAll();

        Assert.Empty(result.CommittedDocuments);
        Assert.True(result.IsPartial);
    }

    [Fact]
    public void CommitAll_ReportsACleanRollbackAsRolledBack_NotAsUnknown()
    {
        // The honesty fix must not swing the other way and start hedging about rollbacks that worked.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("ambient", journal), isAmbient: true);
        set.Open(new JournalingDocumentAdapter("created", journal) { ThrowOnCommit = true });

        var result = set.CommitAll();

        Assert.Equal(new[] { "created", "ambient (active document)" }, result.RolledBackDocuments);
        Assert.Empty(result.UnknownStateDocuments);
    }

    // ---------------------------------------------------------------------------------------------
    // Independent PR review, finding 4: CommitAll's "never throws" contract, and the safety net that
    // silently stopped working when it was violated.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void CommitAll_DoesNotThrow_WhenTheCommitFailuresGetterItselfThrows()
    {
        // CommitFailures was read OUTSIDE every try/catch, on both the success path and the
        // Revit-forced-rollback path. A throwing getter escaped CommitAll entirely, which the executor
        // is not written to survive: it reports a commit failure by INSPECTING the returned result.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(
            new JournalingDocumentAdapter("ambient", journal) { ThrowOnReadingCommitFailures = true },
            isAmbient: true);

        var result = set.CommitAll();

        Assert.True(result.Success);
        // Not silently dropped either (PRD §01): the unreadable failure list is itself reported as an
        // error-severity failure, which reaches notices[] through the ordinary path.
        var reported = Assert.Single(result.CommitFailures);
        Assert.True(reported.IsError);
        Assert.Contains("could not be read", reported.Message);
    }

    [Fact]
    public void CommitAll_LeavesNothingOpen_WhenTheCommitFailuresGetterThrowsMidRun()
    {
        // THE ACTUAL DAMAGE the escaping exception caused: _entries was cleared UP FRONT, so the
        // executor's `finally` RollBackAll() found an empty set and became a no-op, leaking every
        // not-yet-committed document's Transaction and TransactionGroup into the live Revit session.
        // Clearing in a `finally` instead means the set is empty only once CommitAll has genuinely
        // closed everything -- and Count is the observable the executor's net keys off.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("ambient", journal), isAmbient: true);
        set.Open(new JournalingDocumentAdapter("created", journal) { ThrowOnReadingCommitFailures = true });

        var result = set.CommitAll();

        Assert.NotNull(result);
        Assert.Equal(0, set.Count);

        // And the net stays idempotent: nothing is rolled back a second time.
        journal.Clear();
        set.RollBackAll();
        Assert.Empty(journal);
    }

    [Fact]
    public void CommitAll_ReportsADocumentWhoseTitleCannotBeRead_InsteadOfLettingTheThrowEscape()
    {
        // SECOND-ROUND REVIEW FINDING -- AND THIS TEST USED TO PIN THE BUG. Entry.Describe() reads
        // Document.Title, a live Revit call that can throw, and CommitInOrder called it directly on
        // three lines instead of through the SafeDescribe helper two methods away. A Title failure
        // AFTER a successful commit therefore escaped CommitAll (whose contract says it never throws),
        // then escaped TransactionScriptExecutor.ExecuteAsync -- whose try has a finally but no catch --
        // as a raw unhandled exception, destroying the `script-partial-commit` notice that names which
        // documents committed, i.e. exactly the information that notice exists to carry. The previous
        // version of this test asserted the escape (Assert.Throws) and so pinned the buggy behaviour.
        //
        // Fixed behaviour asserted here: CommitAll does not throw, the commits still land and are still
        // REPORTED, and the document that could not name itself appears under SafeDescribe's placeholder
        // rather than vanishing from the report.
        //
        // The deliberate "clear the entry list only on the normal return path, never in a finally"
        // structure that this test used to exercise stays in CommitAll as defence in depth for an
        // unforeseen throw -- there is simply no adapter surface left that can produce one, which is the
        // point of the fix rather than a gap in it.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("ambient", journal), isAmbient: true);
        set.Open(new ThrowingTitleDocumentAdapter("created", journal));

        var result = set.CommitAll();

        Assert.True(result.Success);
        Assert.Equal(2, result.CommittedDocuments.Count);
        Assert.Contains(result.CommittedDocuments, d => d.Contains("could not be read"));
        Assert.Contains(result.CommittedDocuments, d => d.Contains("ambient"));

        // Everything really was closed, so the executor's `finally` net correctly finds nothing to undo.
        Assert.Equal(0, set.Count);
        journal.Clear();
        set.RollBackAll();
        Assert.Empty(journal);
    }

    /// <summary>
    /// Commits fine but throws from Title, so the throw comes out of Entry.Describe() -- the spot that
    /// used to sit outside every guarded adapter call and now routes through SafeDescribe.
    /// </summary>
    private sealed class ThrowingTitleDocumentAdapter : IDocumentAdapter
    {
        private readonly List<string> _journal;
        private readonly string _name;

        public ThrowingTitleDocumentAdapter(string name, List<string> journal)
        {
            _name = name;
            _journal = journal;
        }

        public string Title => throw new InvalidOperationException($"{_name}: simulated Title failure");
        public string? PathName => null;
        public bool IsWorkshared => false;
        public string? CentralModelPath => null;
        public string DocumentId => "tmp-" + _name;

        public ITransactionAdapter CreateTransaction(string name) => new Transaction(this);

        public ITransactionGroupAdapter CreateTransactionGroup(string name) => new Group(this);

        private void Record(string call) => _journal.Add($"{_name}:{call}");

        private sealed class Transaction : ITransactionAdapter
        {
            private readonly ThrowingTitleDocumentAdapter _owner;

            public Transaction(ThrowingTitleDocumentAdapter owner) => _owner = owner;

            public IReadOnlyList<FailureSummary> CommitFailures => Array.Empty<FailureSummary>();

            public void Start() => _owner.Record("tx.Start");

            public TransactionCommitResult Commit()
            {
                _owner.Record("tx.Commit");
                return TransactionCommitResult.Committed;
            }

            public void RollBack() => _owner.Record("tx.RollBack");

            public void Dispose() => _owner.Record("tx.Dispose");
        }

        private sealed class Group : ITransactionGroupAdapter
        {
            private readonly ThrowingTitleDocumentAdapter _owner;

            public Group(ThrowingTitleDocumentAdapter owner) => _owner = owner;

            public void Start() => _owner.Record("group.Start");

            public void Assimilate() => _owner.Record("group.Assimilate");

            public void Dispose() => _owner.Record("group.Dispose");

            public void RollBack() => _owner.Record("group.RollBack");
        }
    }

    [Fact]
    public void CommitAll_StillReportsARevitForcedRollback_WhenTheFailureMessageCannotBeRead()
    {
        // The forced-rollback branch reads CommitFailures a SECOND time, to name the error in the
        // exception it constructs. That read must not throw either, and losing the message must not
        // lose the failure.
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new ForcedRollbackDocumentAdapter("ambient", journal);
        set.Open(ambient, isAmbient: true);

        var result = set.CommitAll();

        Assert.False(result.Success);
        Assert.Contains("forced a rollback", result.Failure!.Message);
    }

    /// <summary>
    /// Reports TransactionCommitResult.RolledBack from Commit() and THEN makes CommitFailures throw --
    /// the exact interleaving the forced-rollback branch's own second read has to survive, which the
    /// single ThrowOnReadingCommitFailures flag cannot express (it would throw on the first read too,
    /// before the branch is ever reached).
    /// </summary>
    private sealed class ForcedRollbackDocumentAdapter : IDocumentAdapter
    {
        private readonly List<string> _journal;

        public ForcedRollbackDocumentAdapter(string title, List<string> journal)
        {
            Title = title;
            _journal = journal;
        }

        public string Title { get; }
        public string? PathName => null;
        public bool IsWorkshared => false;
        public string? CentralModelPath => null;
        public string DocumentId => "tmp-" + Title;

        public ITransactionAdapter CreateTransaction(string name) => new Transaction(this);

        public ITransactionGroupAdapter CreateTransactionGroup(string name) => new Group(this);

        private void Record(string call) => _journal.Add($"{Title}:{call}");

        private sealed class Transaction : ITransactionAdapter
        {
            private readonly ForcedRollbackDocumentAdapter _owner;
            private bool _committed;

            public Transaction(ForcedRollbackDocumentAdapter owner) => _owner = owner;

            public IReadOnlyList<FailureSummary> CommitFailures => _committed
                ? throw new InvalidOperationException("simulated CommitFailures read failure after commit")
                : Array.Empty<FailureSummary>();

            public void Start() => _owner.Record("tx.Start");

            public TransactionCommitResult Commit()
            {
                _owner.Record("tx.Commit");
                _committed = true;
                return TransactionCommitResult.RolledBack;
            }

            public void RollBack() => _owner.Record("tx.RollBack");

            public void Dispose() => _owner.Record("tx.Dispose");
        }

        private sealed class Group : ITransactionGroupAdapter
        {
            private readonly ForcedRollbackDocumentAdapter _owner;

            public Group(ForcedRollbackDocumentAdapter owner) => _owner = owner;

            public void Dispose() => _owner.Record("group.Dispose");

            public void Start() => _owner.Record("group.Start");

            public void Assimilate() => _owner.Record("group.Assimilate");

            public void RollBack() => _owner.Record("group.RollBack");
        }
    }

    private sealed class NonCreatingUiApplicationAdapter : IUiApplicationAdapter
    {
        public IUiDocumentAdapter? ActiveUiDocument => null;

        public System.Collections.Generic.IReadOnlyList<OpenDocumentInfo> OpenDocuments => System.Array.Empty<OpenDocumentInfo>();

        public IDocumentAdapter? FindOpenDocument(string documentId) => null;
    }

    // ---------------------------------------------------------------------------------------------
    // Independent PR review, finding 3: RequireExistingDocumentSource was split out of OpenExisting
    // specifically so this "does this run's adapter support OpenForWriting at all" check is tier-1
    // testable on its own -- see that method's own doc comment. This test is what actually exercises
    // it; without it the split existed but nothing pinned the behaviour it exists to make testable.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void RequireExistingDocumentSource_FailsWithASignpostedMessage_WhenTheAdapterCannotOpenExistingDocuments()
    {
        // NonCreatingUiApplicationAdapter implements neither IDocumentCreationSource nor
        // IExistingDocumentSource -- exactly the tier-1 case OpenForWriting needs a clear, actionable
        // error for, mirroring CreateAndOpenProjectDocument's own signposted-NotSupportedException test
        // above.
        var set = new ManagedDocumentTransactions(TransactionName, new NonCreatingUiApplicationAdapter());

        var ex = Assert.Throws<NotSupportedException>(() => set.RequireExistingDocumentSource());

        Assert.Contains(nameof(IExistingDocumentSource), ex.Message);
        Assert.Contains("revit/test-harness", ex.Message);
    }
}
