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

        /// <summary>Makes the group's SetName throw -- the cosmetic rename Revit might refuse (#146 Phase 2b).</summary>
        public bool ThrowOnSetName { get; set; }

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

            public void SetName(string name)
            {
                _owner.Record("group.SetName:" + name);
                if (_owner.ThrowOnSetName)
                {
                    throw new InvalidOperationException($"{_owner.Title}: simulated SetName refusal");
                }
            }

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

    /// <summary>
    /// One (empty) WithTransaction block on <paramref name="document"/>, so its group has something
    /// committed into it and CommitAll ASSIMILATES rather than rolling an empty group back (#146 Phase 3).
    /// Tests about commit ordering and partial-commit reporting need a group worth keeping.
    /// </summary>
    private static void Write(ManagedDocumentTransactions set, IDocumentAdapter document) =>
        set.RunWithTransactionCore(document, () => { });

    private static FailureSummary Error(string message) => new(isError: true, message, "err", new[] { "1" });

    /// <summary>
    /// A WARNING-severity failure -- commits rather than forcing a rollback, which is what the
    /// failure-accumulation test needs: it has to survive several successful commits in one run.
    /// </summary>
    private static FailureSummary Warning(string message) => new(isError: false, message, "warn", new[] { "1" });


    [Fact]
    public void CreatedDocuments_ReportsOnlyDocumentsCreatedThisRun_NotAmbientOrAdopted()
    {
        // #122: only CreatedThisRun documents outlive the run with no other handle. The ambient document
        // and an block-adopted one existed before the run, so they must NOT be reported as created.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("work", journal), isAmbient: true);          // Ambient
        set.OpenAdoptedForTesting(new JournalingDocumentAdapter("existing", journal));       // AdoptedExisting
        set.Open(new JournalingDocumentAdapter("Project1", journal));                        // CreatedThisRun
        set.Open(new JournalingDocumentAdapter("Family1", journal));                         // CreatedThisRun

        var created = set.CreatedDocuments;

        Assert.Equal(2, created.Count);
        Assert.Contains(created, d => d.Title == "Project1" && d.DocumentId == "tmp-Project1");
        Assert.Contains(created, d => d.Title == "Family1" && d.DocumentId == "tmp-Family1");
        Assert.DoesNotContain(created, d => d.Title == "work");      // ambient excluded
        Assert.DoesNotContain(created, d => d.Title == "existing");  // adopted excluded
    }

    [Fact]
    public void CreatedDocuments_SurvivesRollBackAll_SoAFailedRunCanStillReportThem()
    {
        // The heart of #122: a script that creates documents then throws rolls back, and RollBackAll drops
        // the entry set -- but the created documents STILL EXIST (rollback undoes content, not existence).
        // The captured identities must survive that drop, or the error path has no handle to report. This
        // fails if CreatedDocuments read the entry set instead of a persistent capture.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("work", journal), isAmbient: true);
        set.Open(new JournalingDocumentAdapter("Project1", journal));  // CreatedThisRun

        set.RollBackAll();

        Assert.Equal(0, set.Count); // the entry set is gone...
        var created = set.CreatedDocuments;
        Assert.Single(created);     // ...but the created-document identity survived.
        Assert.Equal("Project1", created[0].Title);
        Assert.Equal("tmp-Project1", created[0].DocumentId);
    }

    [Fact]
    public void CreatedDocuments_CapturesADocumentWhoseTitleThrows_WithoutLettingTheThrowEscapeOpen()
    {
        // #122 (review): Title is a live Revit call that can throw for a document mid-transition (the same
        // reason Describe routes through SafeDescribe). Capturing it must degrade to a placeholder, never
        // escape Open -- the document_id is still a usable handle for a later close/save.
        var journal = new List<string>();
        var set = NewSet();

        set.Open(new ThrowingTitleDocumentAdapter("created", journal)); // must not throw from the Title read

        var created = Assert.Single(set.CreatedDocuments);
        Assert.Equal("tmp-created", created.DocumentId); // the real handle survived
        Assert.Contains("unavailable", created.Title);   // Title degraded to the placeholder
    }

    [Fact]
    public void CreatedDocuments_DoesNotDoubleCapture_WhenACreatedDocumentIsReopenedAfterItsEntryWasDropped()
    {
        // #122 (review): a created document that is settled (which empties the entry set) and then re-Opened
        // -- via a fresh WithTransaction/WithTransaction-adoption -- passes the duplicate-open guard (its entry is gone)
        // but must not be captured twice. RollBackAll drops the entry set the same way a settle does, so
        // re-Opening after it exercises the de-dup path deterministically.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("Project1", journal)); // CreatedThisRun -- captured
        set.RollBackAll();                                            // drops _entries, keeps the capture
        set.Open(new JournalingDocumentAdapter("Project1", journal)); // same document_id, entry gone -> no guard

        Assert.Single(set.CreatedDocuments); // de-duped by document_id, still exactly one
    }


    [Fact]
    public void Open_ThrowsWhenCalledTwiceForTheSameDocumentId()
    {
        // WithTransaction-adoption (ScriptGlobals) specifically targets a document that may already be tracked --
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
        // CreateFamilyDocument/WithTransaction-adoption...") that names "ambient" as a documented CASE, not the
        // document this guard actually named. Assert on the real SafeDescribe output instead, so this
        // test genuinely fails if the guard ever names the wrong document.
        Assert.Contains("ambient (active document)", ex.Message);
        Assert.Equal(1, set.Count); // the duplicate must not have been added alongside the original.
        // No TransactionGroup was started for the rejected duplicate -- the guard fires before any
        // Revit-side allocation, not after. Journal still holds exactly the first Open's entry.
        Assert.Equal(new[] { "ambient:group.Start" }, journal);
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
        var ambient = new JournalingDocumentAdapter("ambient", journal);
        var created1 = new JournalingDocumentAdapter("created-1", journal);
        var created2 = new JournalingDocumentAdapter("created-2", journal);
        set.Open(ambient, isAmbient: true);
        set.Open(created1);
        set.Open(created2);
        Write(set, ambient);
        Write(set, created1);
        Write(set, created2);
        journal.Clear();

        var result = set.CommitAll();

        Assert.True(result.Success);
        Assert.Equal(
            new[] { "created-1:group.Assimilate", "created-2:group.Assimilate", "ambient:group.Assimilate" },
            journal.Where(c => c.Contains("Assimilate")).ToArray());
        Assert.Equal(new[] { "created-1", "created-2", "ambient (active document)" }, result.CommittedDocuments);
        Assert.Empty(result.RolledBackDocuments);
        Assert.False(result.IsPartial);
    }

    // ---------------------------------------------------------------------------------------------
    // Independent PR review (2nd round): DocumentOrigin.AdoptedExisting had ZERO tier-1 coverage --
    // not the 3-tier commit ordering, not Describe()'s "(adopted via WithTransaction-adoption)" branch, not the
    // AnyCommittedDocumentMayBeReal true branch. All three below use OpenAdoptedForTesting, the
    // internal test-only entry point added for exactly this gap.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void CommitAll_CommitsCreatedDocumentsThenAdoptedThenAmbientLast()
    {
        // The three-tier ordering this class' own doc comment describes: CreatedThisRun (safest,
        // unsaved/in-memory) first, AdoptedExisting (may be a real saved model WithTransaction-adoption adopted)
        // next, Ambient always last -- collapsing AdoptedExisting into the CreatedThisRun bucket would
        // let a real adopted document commit alongside genuinely-throwaway ones, exactly the ordering
        // mistake DocumentOrigin's own doc comment exists to prevent.
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new JournalingDocumentAdapter("ambient", journal);
        var created = new JournalingDocumentAdapter("created", journal);
        var adopted = new JournalingDocumentAdapter("adopted", journal);
        set.Open(ambient, isAmbient: true);
        set.Open(created);
        set.OpenAdoptedForTesting(adopted);
        Write(set, ambient);
        Write(set, created);
        Write(set, adopted);

        var result = set.CommitAll();

        Assert.True(result.Success);
        Assert.Equal(
            new[] { "created", "adopted (adopted via WithTransaction)", "ambient (active document)" },
            result.CommittedDocuments);
    }

    [Fact]
    public void Entry_Describe_NamesAnAdoptedDocumentDistinctlyFromACreatedOne()
    {
        var journal = new List<string>();
        var set = NewSet();
        var adopted = new JournalingDocumentAdapter("adopted", journal);
        set.OpenAdoptedForTesting(adopted);
        Write(set, adopted);

        var result = set.CommitAll();

        Assert.Equal(new[] { "adopted (adopted via WithTransaction)" }, result.CommittedDocuments);
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
        var adopted = new JournalingDocumentAdapter("adopted", journal);
        set.OpenAdoptedForTesting(adopted);
        Write(set, adopted);

        var result = set.CommitAll();

        Assert.True(result.AnyCommittedDocumentMayBeReal);
    }

    [Fact]
    public void CommitAll_DoesNotSetAnyCommittedDocumentMayBeReal_WhenOnlyCreatedDocumentsCommit()
    {
        // The false branch, for contrast -- a run with only CreatedThisRun documents (the pre-WithTransaction-adoption
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
        // WithTransaction-adoption's headline safety guarantee: a thrown script rolls back an adopted document's
        // writes exactly like any other managed document. Nothing about AdoptedExisting changes
        // RollBackAll's behavior -- this pins that explicitly rather than leaving it to be re-derived
        // from the fact that RollBackAll doesn't branch on Origin at all.
        var journal = new List<string>();
        var set = NewSet();
        set.OpenAdoptedForTesting(new JournalingDocumentAdapter("adopted", journal));
        journal.Clear();

        set.RollBackAll();

        Assert.Equal(new[] { "adopted:group.RollBack", "adopted:group.Dispose" }, journal);
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
        Write(set, first);
        Write(set, second);
        ((FakeTransactionGroupAdapter)first.LastTransactionGroup!).ThrowOnDispose = true;

        var result = set.CommitAll();

        Assert.True(result.Success);
        // The first entry's group dispose threw; the outcome is unchanged and the SECOND entry's group is
        // still disposed (SafeDispose guards each entry's disposal independently).
        Assert.Contains("Dispose", ((FakeTransactionGroupAdapter)first.LastTransactionGroup!).Calls);
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
        Write(set, created);    // failures surface at each block's commit (#146 Phase 3)...
        Write(set, ambient);

        var result = set.CommitAll();

        Assert.True(result.Success);
        // ...and are reported in commit ORDER (created first, ambient last), not block order.
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
            new[] { "created:group.RollBack", "created:group.Dispose", "ambient:group.RollBack", "ambient:group.Dispose" },
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
        var ambient = new JournalingDocumentAdapter("ambient", journal);
        var created = new JournalingDocumentAdapter("created", journal) { ThrowOnAssimilate = true };
        set.Open(ambient, isAmbient: true);
        set.Open(created);
        Write(set, created);   // #146 Phase 3: a block's commit failure throws INTO the script; what can
        Write(set, ambient);   // still fail at CommitAll is the group's Assimilate.
        journal.Clear();

        var result = set.CommitAll();

        Assert.False(result.Success);
        Assert.False(result.IsPartial);
        Assert.Empty(result.CommittedDocuments);
        Assert.Equal(new[] { "created", "ambient (active document)" }, result.RolledBackDocuments);
        Assert.Equal(
            new[]
            {
                "created:group.Assimilate", "created:group.RollBack", "created:group.Dispose",
                "ambient:group.RollBack", "ambient:group.Dispose",
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
        var ambient = new JournalingDocumentAdapter("ambient", journal);
        var created1 = new JournalingDocumentAdapter("created-1", journal);
        var created2 = new JournalingDocumentAdapter("created-2", journal) { ThrowOnAssimilate = true };
        set.Open(ambient, isAmbient: true);
        set.Open(created1);
        set.Open(created2);
        Write(set, ambient);
        Write(set, created1);
        Write(set, created2);

        var result = set.CommitAll();

        Assert.False(result.Success);
        Assert.True(result.IsPartial);
        Assert.Equal(new[] { "created-1" }, result.CommittedDocuments);
        Assert.Equal(new[] { "created-2", "ambient (active document)" }, result.RolledBackDocuments);
        Assert.Contains("ambient:group.RollBack", journal);
        Assert.DoesNotContain("ambient:group.Assimilate", journal);
    }

    [Fact]
    public void WithTransaction_DoesNotRollBackTheTransactionItself_WhenRevitAlreadyRolledItBack()
    {
        // TransactionCommitResult.RolledBack means Revit's own Failures API already closed the
        // Transaction (ProceedWithRollBack); calling RollBack() on it again would be invalid. Since #146
        // Phase 3 every commit happens at a block's end, so this surfaces INTO the script as the block's
        // exception, carrying the reason -- and the block's unwind must not touch the closed transaction.
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new JournalingDocumentAdapter("ambient", journal)
        {
            FailuresToReport = new[] { Error("a hard failure") },
        };
        set.Open(ambient, isAmbient: true);
        journal.Clear();

        var ex = Assert.Throws<InvalidOperationException>(() => set.RunWithTransactionCore(ambient, () => { }));

        Assert.Contains("a hard failure", ex.Message);
        Assert.Equal(new[] { "ambient:tx.Start", "ambient:tx.Commit", "ambient:tx.Dispose" }, journal);   // no tx.RollBack
        // Nothing committed into the group, so the run ends with an empty group rolled back.
        journal.Clear();
        var result = set.CommitAll();
        Assert.True(result.Success);
        Assert.Equal(new[] { "ambient:group.RollBack", "ambient:group.Dispose" }, journal);
        Assert.Single(result.CommitFailures);
    }

    [Fact]
    public void CommitAll_ReportsFailure_WhenAssimilateThrowsAfterAGoodCommit()
    {
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new JournalingDocumentAdapter("ambient", journal) { ThrowOnAssimilate = true };
        set.Open(ambient, isAmbient: true);
        Write(set, ambient);
        journal.Clear();

        var result = set.CommitAll();

        Assert.False(result.Success);
        Assert.Equal(new[] { "ambient:group.Assimilate", "ambient:group.RollBack", "ambient:group.Dispose" }, journal);
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
        // Group only (#146 Phase 3, H2): a created document is writable through WithTransaction exactly like
        // the ambient one -- one default, not two.
        Assert.Equal(new[] { "created:group.Start" }, journal);
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
        Assert.Equal(new[] { "created-family:group.Start" }, journal);
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
        var ambient = new JournalingDocumentAdapter("ambient", journal) { ThrowOnGroupRollBack = true };
        var created = new JournalingDocumentAdapter("created", journal) { ThrowOnAssimilate = true };
        set.Open(ambient, isAmbient: true);
        set.Open(created);
        Write(set, created);
        Write(set, ambient);

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
        var ambient = new JournalingDocumentAdapter("ambient", journal) { ThrowOnGroupRollBack = true };
        var created = new JournalingDocumentAdapter("created", journal) { ThrowOnAssimilate = true };
        set.Open(ambient, isAmbient: true);
        set.Open(created);
        Write(set, created);

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
        var ambient = new JournalingDocumentAdapter("ambient", journal)
        {
            ThrowOnAssimilate = true,
            ThrowOnGroupRollBack = true,
        };
        set.Open(ambient, isAmbient: true);
        Write(set, ambient);

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
        var created = new JournalingDocumentAdapter("created", journal) { ThrowOnAssimilate = true };
        set.Open(new JournalingDocumentAdapter("ambient", journal), isAmbient: true);
        set.Open(created);
        Write(set, created);

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
        var ambient = new JournalingDocumentAdapter("ambient", journal) { ThrowOnReadingCommitFailures = true };
        set.Open(ambient, isAmbient: true);
        Write(set, ambient);   // the getter is read at the block's commit (#146 Phase 3)

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
        var ambient = new JournalingDocumentAdapter("ambient", journal);
        var created = new ThrowingTitleDocumentAdapter("created", journal);
        set.Open(ambient, isAmbient: true);
        set.Open(created);
        Write(set, ambient);
        Write(set, created);

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

            public void SetName(string name) => _owner.Record("group.SetName:" + name);

            public void Assimilate() => _owner.Record("group.Assimilate");

            public void Dispose() => _owner.Record("group.Dispose");

            public void RollBack() => _owner.Record("group.RollBack");
        }
    }

    [Fact]
    public void WithTransaction_StillReportsARevitForcedRollback_WhenTheFailureMessageCannotBeRead()
    {
        // Since #146 Phase 3 the commit -- and Revit's forced rollback -- happens at the block's end, so
        // the reason (or the fact that it could not be read) reaches the script as the block's exception.
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new ForcedRollbackDocumentAdapter("ambient", journal);
        set.Open(ambient, isAmbient: true);

        var ex = Assert.Throws<InvalidOperationException>(() => set.RunWithTransactionCore(ambient, () => { }));

        Assert.Contains("rolled back", ex.Message);
        Assert.Contains("could not be read", ex.Message);
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

            public void SetName(string name) => _owner.Record("group.SetName:" + name);

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
    // specifically so this "does this run's adapter support WithTransaction-adoption at all" check is tier-1
    // testable on its own -- see that method's own doc comment. This test is what actually exercises
    // it; without it the split existed but nothing pinned the behaviour it exists to make testable.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void RequireExistingDocumentSource_FailsWithASignpostedMessage_WhenTheAdapterCannotOpenExistingDocuments()
    {
        // NonCreatingUiApplicationAdapter implements neither IDocumentCreationSource nor
        // IExistingDocumentSource -- exactly the tier-1 case WithTransaction-adoption needs a clear, actionable
        // error for, mirroring CreateAndOpenProjectDocument's own signposted-NotSupportedException test
        // above.
        var set = new ManagedDocumentTransactions(TransactionName, new NonCreatingUiApplicationAdapter());

        var ex = Assert.Throws<NotSupportedException>(() => set.RequireExistingDocumentSource());

        Assert.Contains(nameof(IExistingDocumentSource), ex.Message);
        Assert.Contains("revit/test-harness", ex.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // settle-on-request (issue #132). These are ORDERING tests above all: every one of the three
    // scopes is defined by WHEN the connector opens and closes things, not by what it returns, so the
    // shared journal -- not a return value -- is what can actually fail here.
    // ---------------------------------------------------------------------------------------------







    [Fact]
    public void WithTransaction_RefusesNestingInsideAnotherWithTransactionOnTheSameDocument()
    {
        // Decision 1 (#132): refuse loudly rather than join transparently. Joining would make "the
        // connector commits at block end" false for the inner block, and would let a caught inner
        // failure ride silently on the outer commit.
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("ambient", journal);
        set.Open(document, isAmbient: true);

        // ACTUALLY NESTED: WithTransaction-inside-WithTransaction, which is the shape the name and the
        // decision are about (every Revit sample wraps each helper method in its own transaction, so
        // agents write it).
        Exception? inner = null;
        set.RunWithTransactionCore(document, () =>
            inner = Record.Exception(() => set.RunWithTransactionCore(document, () => { })));

        var ex = Assert.IsType<InvalidOperationException>(inner);
        Assert.Contains("cannot be nested on the same document", ex.Message);
        Assert.Contains("Write directly instead", ex.Message);
    }

    [Fact]
    public void WithTransaction_AccumulatesFailuresFromEveryCommitNotJustTheLast()
    {
        // The data-loss bug review found: ITransactionAdapter.CommitFailures is overwritten on every
        // Commit(), so reading it once at end-of-run destroys the first N-1 sets. Two scoped commits
        // plus the end-of-run one must all be represented.
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("ambient", journal)
        {
            FailuresToReport = new[] { Warning("off axis") },
        };
        set.Open(document, isAmbient: true);

        // Three blocks, three commits, three distinct failure sets -- under group-always there is no
        // end-of-run commit, so every failure a run can report comes from a block.
        set.RunWithTransactionCore(document, () => { });
        document.FailuresToReport = new[] { Warning("second") };
        set.RunWithTransactionCore(document, () => { });
        document.FailuresToReport = new[] { Warning("third") };
        set.RunWithTransactionCore(document, () => { });
        var result = set.CommitAll();

        Assert.True(result.Success);
        // DISTINCT per commit, deliberately: with one shared fixture string the collection assertion
        // passed for any subset, so only the count opposed the read-once mutation (caveats.md -- check
        // the fixture actually opposes it). Named messages make every dropped set visible.
        Assert.Equal(
            new[] { "off axis", "second", "third" },
            result.CommitFailures.Select(f => f.Message).ToArray());
    }


    [Fact]
    public void Settle_KeepAssimilatesTheGroupAndDeregistersTheDocument()
    {
        // Deregistration is what keeps the unwind correct: CommitAll walks every entry, so a settled
        // pair left in the set would be assimilated a SECOND time.
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new JournalingDocumentAdapter("ambient", journal);
        var scratch = new JournalingDocumentAdapter("scratch", journal);
        set.Open(ambient, isAmbient: true);
        set.OpenAdoptedForTesting(scratch);
        journal.Clear();

        Write(set, scratch);
        journal.Clear();
        set.SettleCore(scratch, keep: true);

        Assert.Equal(new[] { "scratch:group.Assimilate", "scratch:group.Dispose" }, journal);
        Assert.Equal(1, set.Count);

        journal.Clear();
        var result = set.CommitAll();
        Assert.True(result.Success);
        Assert.DoesNotContain(journal, entry => entry.StartsWith("scratch:", StringComparison.Ordinal));
    }

    [Fact]
    public void Settle_DiscardRollsTheGroupBackAndDeregisters()
    {
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new JournalingDocumentAdapter("ambient", journal);
        var scratch = new JournalingDocumentAdapter("scratch", journal);
        set.Open(ambient, isAmbient: true);
        set.OpenAdoptedForTesting(scratch);
        journal.Clear();

        Write(set, scratch);
        journal.Clear();
        set.SettleCore(scratch, keep: false);

        // The block's transaction was committed into the group at block end; discarding is the GROUP's
        // rollback (which discards that committed transaction -- the whole point of the group boundary).
        Assert.Equal(new[] { "scratch:group.RollBack", "scratch:group.Dispose" }, journal);
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void Settle_IsRecordedSoTheExecutorCanRaiseTheNotice()
    {
        // Decision 2 (#132): Settle is the irreversible one, so it is the only scope that notices.
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new JournalingDocumentAdapter("ambient", journal);
        var scratch = new JournalingDocumentAdapter("scratch", journal);
        set.Open(ambient, isAmbient: true);
        set.OpenAdoptedForTesting(scratch);

        set.SettleCore(scratch, keep: true);
        set.SettleCore(ambient, keep: false);

        Assert.Collection(
            set.Settlements,
            first =>
            {
                Assert.Equal("scratch (adopted via WithTransaction)", first.Document);
                Assert.True(first.Kept);
            },
            second =>
            {
                Assert.Equal("ambient (active document)", second.Document);
                Assert.False(second.Kept);
            });
    }


    [Fact]
    public void Settle_ThrowsForADocumentThisRunDoesNotManage()
    {
        var journal = new List<string>();
        var set = NewSet();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            set.SettleCore(new JournalingDocumentAdapter("stranger", journal), keep: true));

        Assert.Contains("does not manage", ex.Message);
    }

    [Fact]
    public void WritingAgainAfterSettle_OpensAFreshGroupRatherThanBeingRefused()
    {
        // Open's DocumentId guard only refuses while an entry EXISTS, so deregistering at settle is
        // what makes a post-settle write possible at all. Review flagged this as an unnamed constraint.
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("ambient", journal);
        set.Open(document, isAmbient: true);
        set.SettleCore(document, keep: true);
        journal.Clear();

        set.RunWithTransactionCore(document, () => journal.Add("LATER-WRITE"));

        Assert.Equal(
            new[] { "ambient:group.Start", "ambient:tx.Start", "LATER-WRITE", "ambient:tx.Commit", "ambient:tx.Dispose" },
            journal);
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void CommitAll_AssimilatesWithoutASecondCommit_WhenAScopeAlreadyClosedTheTransaction()
    {
        // An entry whose transaction is null is not an error state -- it is a document a WithTransaction
        // block already committed into its group. Only the group remains.
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("ambient", journal);
        set.Open(document, isAmbient: true);
        set.SettleCore(document, keep: true);
        set.RunWithTransactionCore(document, () => { });
        journal.Clear();

        var result = set.CommitAll();

        Assert.True(result.Success);
        // NO tx.Dispose here, and that is correct rather than a gap: the scope disposed the transaction
        // at its own terminal point (issue #34 -- dispose strictly after that entry's handling settles),
        // so CommitAll has only the group left to close. An earlier draft of this test asserted a second
        // tx.Dispose and failed, which is the assertion catching a double-dispose that never happens.
        Assert.Equal(new[] { "ambient:group.Assimilate", "ambient:group.Dispose" }, journal);
    }

    [Fact]
    public void WithTransaction_RollsItsOwnBlockBackWhenTheBodyThrows_AndLeavesTheDocumentUsable()
    {
        // THE WEDGE. Relying on RollBackAll only works for an UNCAUGHT exception; a script that catches
        // -- ordinary code, "try this API, fall back if it fails" -- used to find every later
        // WithTransaction on that document refused for the rest of the run, and the failed body's
        // partial writes still sitting in an open transaction for CommitAll to make permanent.
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("ambient", journal);
        set.Open(document, isAmbient: true);

        var thrown = Record.Exception(() =>
            set.RunWithTransactionCore(document, () => throw new InvalidOperationException("body failed")));
        Assert.Equal("body failed", thrown?.Message);

        journal.Clear();
        // The recovery that used to be impossible.
        set.RunWithTransactionCore(document, () => journal.Add("SECOND-BLOCK-RAN"));

        Assert.Equal(
            new[] { "ambient:tx.Start", "SECOND-BLOCK-RAN", "ambient:tx.Commit", "ambient:tx.Dispose" },
            journal);
    }

    [Fact]
    public void WithTransaction_ThatOpenedTheGroupItself_UnwindsAndDeregistersWhenTheBodyThrows()
    {
        // Nothing was committed into that group, so leaving it open would strand one the script never
        // asked for -- and deregistering is what lets a retry open a clean one.
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new JournalingDocumentAdapter("ambient", journal);
        var fresh = new JournalingDocumentAdapter("fresh", journal);
        set.Open(ambient, isAmbient: true);
        journal.Clear();

        Assert.Throws<InvalidOperationException>(() =>
            set.RunWithTransactionCore(fresh, () => throw new InvalidOperationException("body failed")));

        Assert.Equal(
            new[] { "fresh:group.Start", "fresh:tx.Start", "fresh:tx.RollBack", "fresh:tx.Dispose", "fresh:group.RollBack", "fresh:group.Dispose" },
            journal);
        Assert.Equal(1, set.Count); // only the ambient document remains
    }

    [Fact]
    public void Settle_LeavesTheDocumentManagedWhenAssimilateThrows()
    {
        // The only terminal path in this class that used to be unguarded. A settle that FAILED has not
        // reached a terminal state -- the group may still be open -- so the entry must STAY registered
        // for the executor's finally-net to close, or an open group leaks into the live session with
        // nothing holding a reference.
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("scratch", journal) { ThrowOnAssimilate = true };
        set.OpenAdoptedForTesting(document);
        Write(set, document);   // something to assimilate, else the empty group is simply rolled back

        var ex = Assert.Throws<InvalidOperationException>(() => set.SettleCore(document, keep: true));

        Assert.Contains("Settling 'scratch (adopted via WithTransaction)'", ex.Message);
        Assert.Contains("still managed by this run", ex.Message);
        Assert.Equal(1, set.Count);
        Assert.Empty(set.Settlements);   // nothing settled, so nothing to notice about
    }

    [Fact]
    public void WritingAgainAfterSettlingTheAmbientDocument_KeepsItCommittingLast()
    {
        // THE ORDERING REGRESSION. Re-opening after a settle used to guess AdoptedExisting, which
        // reclassified the run's ACTIVE document -- a real model a person has open -- as an adopted one,
        // so it committed BEFORE throwaway scratch documents instead of last. That inverts the single
        // guarantee CommitAll's ordering exists to give: that a failure among the created documents is
        // still answerable by rolling the ambient one back.
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new JournalingDocumentAdapter("ambient", journal);
        set.Open(ambient, isAmbient: true);
        set.SettleCore(ambient, keep: true);
        set.RunWithTransactionCore(ambient, () => { });      // re-opened here

        var created = new JournalingDocumentAdapter("created", journal);
        set.Open(created);
        Write(set, created);
        journal.Clear();

        var result = set.CommitAll();

        Assert.True(result.Success);
        // created assimilates BEFORE ambient. Asserted on order, not membership: a membership assertion
        // passes under exactly the bug this test exists for.
        var groupCalls = journal.Where(j => j.EndsWith("group.Assimilate", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "created:group.Assimilate", "ambient:group.Assimilate" }, groupCalls);
        // And it is still DESCRIBED as the active document, which is what a partial-commit or settle
        // notice would name back to the agent.
        Assert.Contains("ambient (active document)", string.Join("|", result.CommittedDocuments));
    }


    [Fact]
    public void ReopenTransaction_DisposesTheAdapterWhenStartThrows()
    {
        // Same care Open takes for the same failure (issue #34). Also untested until review: deleting
        // ReopenTransaction's catch reintroduced the leak with the suite still green, because the PR's
        // mutation evidence ("remove ReopenTransaction -> 5 fail") measured the METHOD's existence, not
        // either guard inside it.
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("ambient", journal);
        set.Open(document, isAmbient: true);   // group only: the resting state has no transaction
        journal.Clear();
        document.ThrowOnStartTransaction = true;
        Assert.Throws<InvalidOperationException>(() => set.RunWithTransactionCore(document, () => { }));

        // The adapter that failed to start is disposed rather than abandoned to a finalizer.
        Assert.Contains("ambient:tx.Dispose", journal);
    }

    [Fact]
    public void ReopenTransaction_WhenStartThrows_ContextualizesTheErrorAndPreservesTheCause()
    {
        // The sibling ReopenTransaction_DisposesTheAdapterWhenStartThrows pins the #34 cleanup; this pins
        // the OTHER half of that catch -- the message. A raw re-throw handed the agent Revit's own "the
        // transaction could not be started", naming neither the document nor that the connector was
        // reopening a transaction (the WithTransaction path), an error two steps from the cause and the
        // exact PRD §01 sin Open/ResolveAdapter/SettleCore already signpost against. Reverting the wrap to
        // a bare `throw` leaves the disposal test green, so this is what actually holds the message.
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("a-document-with-a-name", journal);
        set.Open(document, isAmbient: true);

        document.ThrowOnStartTransaction = true;
        var ex = Assert.Throws<InvalidOperationException>(() => set.RunWithTransactionCore(document, () => { }));

        // Names the document and the action, so the agent sees what failed and where.
        Assert.Contains("a-document-with-a-name", ex.Message);
        Assert.Contains("Reopening a transaction", ex.Message);
        // The raw Revit reason survives as the inner exception rather than being discarded.
        Assert.NotNull(ex.InnerException);
        Assert.Contains("simulated transaction-start failure", ex.InnerException!.Message);
    }

    [Fact]
    public void Settle_PreservesFailuresAccumulatedBeforeItForTheRunsNotices()
    {
        // A settled document leaves the entry set, and CommitAll -- the only reader of
        // AccumulatedFailures -- walks that set. So every Revit warning the document raised before being
        // settled silently never reached notices[]. Found by review; deleting the preservation changes
        // nothing else green.
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new JournalingDocumentAdapter("ambient", journal);
        var scratch = new JournalingDocumentAdapter("scratch", journal)
        {
            FailuresToReport = new[] { Warning("off axis in the scratch document") },
        };
        set.Open(ambient, isAmbient: true);
        set.OpenAdoptedForTesting(scratch);
        Write(set, scratch);   // the warning is raised at the block's commit

        set.SettleCore(scratch, keep: true);
        var result = set.CommitAll();

        Assert.True(result.Success);
        Assert.Contains(result.CommitFailures, f => f.Message == "off axis in the scratch document");
    }

    [Fact]
    public void SettlingInsideAWithTransactionBlockThatThenThrows_DoesNotUnwindTheSettledGroup()
    {
        // RunBody's openedGroupHere unwind used to assume its entry was still registered. Settle
        // assimilates, disposes and deregisters -- so the unwind rolled back an ALREADY-ASSIMILATED,
        // already-disposed group, the exact invalid case SettleCore's own comment names. The Safe*
        // wrappers swallow it, so the symptom was a defeated invariant rather than a crash.
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new JournalingDocumentAdapter("ambient", journal);
        var fresh = new JournalingDocumentAdapter("fresh", journal);
        set.Open(ambient, isAmbient: true);
        journal.Clear();

        Assert.Throws<InvalidOperationException>(() =>
            set.RunWithTransactionCore(fresh, () =>
            {
                set.SettleCore(fresh, keep: true);
                throw new InvalidOperationException("after settling");
            }));

        // Exactly ONE group.RollBack must never appear for `fresh`: it was assimilated, not rolled back.
        Assert.DoesNotContain("fresh:group.RollBack", journal);
        Assert.Contains("fresh:group.Assimilate", journal);
        Assert.Equal(1, set.Count);   // only the ambient document
    }

    [Fact]
    public void ReacquiringTheSettledAmbientDocument_KeepsItsOriginalTier()
    {
        // The origin fix was applied to RunWithTransactionCore only, while OpenExisting -- the adapter
        // half of WithTransaction adoption, and the route WithTransaction-adoption's own XML text sends people
        // down after a settle -- still hardcoded AdoptedExisting. Found by review.
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new JournalingDocumentAdapter("ambient", journal);
        set.Open(ambient, isAmbient: true);
        set.SettleCore(ambient, keep: true);

        // Asserts the DECISION rather than the wiring: OpenExisting needs a real
        // Autodesk.Revit.DB.Document, so it is tier-2 only, but the tier it chooses is what regressed
        // and both re-acquisition points now route through this one rule.
        Assert.Equal(ManagedDocumentTransactions.DocumentOrigin.Ambient, set.OriginForTesting(ambient.DocumentId));
        Assert.Equal(ManagedDocumentTransactions.DocumentOrigin.AdoptedExisting, set.OriginForTesting("tmp-never-seen"));
    }

    // ------------------------------------------------------------------------------------------
    // #146 Phase 2b: the undo label is applied per document, after commit, before assimilate
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void CommitAll_SetsEachDocumentsOwnUndoLabel_AfterCommitAndBeforeAssimilate()
    {
        // The ORDER is the point: the label is derived from the document's net effect, which is only
        // known once its transaction has committed (that raises the last DocumentChanged), and the group
        // can only be renamed before it assimilates. PER DOCUMENT (independent review): a run-wide tally
        // stamped on every entry told a scratch document's Undo menu about walls that live in the model.
        var journal = new List<string>();
        var set = NewSet();
        var created = new JournalingDocumentAdapter("created", journal);
        var ambient = new JournalingDocumentAdapter("ambient", journal);
        set.Open(ambient, isAmbient: true);
        set.Open(created);
        Write(set, created);
        Write(set, ambient);
        journal.Clear();

        var asked = new List<string>();
        var result = set.CommitAll(undoLabel: documentId =>
        {
            asked.Add(documentId);
            return documentId == created.DocumentId ? "MCP: 3 Levels created" : "MCP: 12 Walls created";
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { created.DocumentId, ambient.DocumentId }, asked);
        Assert.Equal(
            new[]
            {
                "created:group.SetName:MCP: 3 Levels created", "created:group.Assimilate", "created:group.Dispose",
                "ambient:group.SetName:MCP: 12 Walls created", "ambient:group.Assimilate", "ambient:group.Dispose",
            },
            journal);
        Assert.Empty(set.UndoLabelFailures);
    }

    [Fact]
    public void CommitAll_ARefusedSetName_IsRecorded_AndNeverFailsTheCommit()
    {
        // Cosmetic by contract, but never silent (review): the rename is a human-visible signal, so its
        // failure must be observable or a Revit version rejecting it would go undetected forever.
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new JournalingDocumentAdapter("ambient", journal) { ThrowOnSetName = true };
        set.Open(ambient, isAmbient: true);
        Write(set, ambient);

        var result = set.CommitAll(undoLabel: _ => "MCP: 1 element created (Walls)");

        Assert.True(result.Success);
        Assert.Contains("ambient:group.Assimilate", journal);
        var failure = Assert.Single(set.UndoLabelFailures);
        Assert.Contains("ambient (active document)", failure);
        Assert.Contains("simulated SetName refusal", failure);
    }

    [Fact]
    public void CommitAll_LeavesTheGroupsNameAlone_WhenNoLabelFunctionOrANullLabelIsGiven()
    {
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new JournalingDocumentAdapter("ambient", journal);
        set.Open(ambient, isAmbient: true);
        Write(set, ambient);

        set.CommitAll();
        Assert.DoesNotContain(journal, j => j.Contains("group.SetName"));

        var again = NewSet();
        var other = new JournalingDocumentAdapter("other", journal);
        again.Open(other, isAmbient: true);
        Write(again, other);
        again.CommitAll(undoLabel: _ => null);
        Assert.DoesNotContain(journal, j => j.Contains("group.SetName"));
    }

    [Fact]
    public void CommitAll_AThrowingLabelFunction_NeverFailsTheCommit()
    {
        // The name is cosmetic; the writes it would label are already permanent by the time it is asked.
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new JournalingDocumentAdapter("ambient", journal);
        set.Open(ambient, isAmbient: true);
        Write(set, ambient);

        var result = set.CommitAll(undoLabel: _ => throw new InvalidOperationException("label blew up"));

        Assert.True(result.Success);
        Assert.Contains("ambient:group.Assimilate", journal);
        Assert.Single(set.UndoLabelFailures);
    }

    // ------------------------------------------------------------------------------------------
    // #146 Phase 0: WithTransaction<T>, and the WithoutTransaction reopen restoring PRIOR state (H1)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void WithTransactionOfT_ReturnsTheBodysValue_AndCommitsAtBlockEnd()
    {
        // The "create X, return its id" shape (#146 H4). With only an Action body the script has to
        // hoist a local out of the block; the generic overload hands the value straight back and must
        // run the SAME open/commit choreography as the Action form -- asserted on the journal so the
        // two cannot drift apart.
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("ambient", journal);
        set.Open(document, isAmbient: true);

        journal.Clear();
        var result = set.RunWithTransactionCore(document, () =>
        {
            journal.Add("BODY");
            return 42;
        });

        Assert.Equal(42, result);
        Assert.Equal(
            new[] { "ambient:tx.Start", "BODY", "ambient:tx.Commit", "ambient:tx.Dispose" },
            journal);
    }

    [Fact]
    public void WithTransactionOfT_RollsItsOwnBlockBackWhenTheBodyThrows_AndLeavesTheDocumentUsable()
    {
        // Same WEDGE/silent-commit guarantee as the Action form -- a generic overload that bypassed
        // RunBody's unwind would reintroduce both for exactly the callers most likely to catch.
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("ambient", journal);
        set.Open(document, isAmbient: true);

        journal.Clear();
        var thrown = Record.Exception(() =>
            set.RunWithTransactionCore<int>(document, () => throw new InvalidOperationException("body failed")));
        Assert.Equal("body failed", thrown?.Message);
        Assert.Equal(new[] { "ambient:tx.Start", "ambient:tx.RollBack", "ambient:tx.Dispose" }, journal);

        // Recovery: the next block on the same document is not refused.
        var second = set.RunWithTransactionCore(document, () => "second");
        Assert.Equal("second", second);
    }



    // ------------------------------------------------------------------------------------------
    // #146 Phase 3: group-always, transaction-on-write
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Open_StartsOnlyTheGroup_TheDocumentIsNotModifiableUntilABlockOpens()
    {
        var journal = new List<string>();
        var set = NewSet();

        set.Open(new JournalingDocumentAdapter("ambient", journal), isAmbient: true);

        Assert.Equal(new[] { "ambient:group.Start" }, journal);
    }

    [Fact]
    public void WithTransaction_OpensATransactionInsideTheRunsGroup_AndClosesItAtBlockEnd()
    {
        // THE write primitive's choreography: the group was opened at run start; the block opens the
        // transaction, runs, commits into the group, and leaves the group open for the next block.
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("ambient", journal);
        set.Open(document, isAmbient: true);
        journal.Clear();

        set.RunWithTransactionCore(document, () => journal.Add("BODY"));
        set.RunWithTransactionCore(document, () => journal.Add("BODY-2"));

        Assert.Equal(
            new[]
            {
                "ambient:tx.Start", "BODY", "ambient:tx.Commit", "ambient:tx.Dispose",
                "ambient:tx.Start", "BODY-2", "ambient:tx.Commit", "ambient:tx.Dispose",
            },
            journal);
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void CommitAll_RollsBackAnEmptyGroup_SoAReadOnlyRunLeavesNoUndoEntry()
    {
        // The deterministic rule from #146: never Assimilate an empty group. Outcome-identical (nothing
        // to keep) and provably undo-invisible whatever Revit does with an empty Assimilate.
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("ambient", journal), isAmbient: true);
        journal.Clear();

        var result = set.CommitAll();

        Assert.True(result.Success);
        Assert.Equal(new[] { "ambient:group.RollBack", "ambient:group.Dispose" }, journal);
        // Closed cleanly but NOT "committed": nothing of it remains, so the partial-commit notice must
        // never count it among documents whose changes stay (independent review of #160).
        Assert.Empty(result.CommittedDocuments);
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void CommitAll_AssimilatesAGroupAnObservedExternalCommitLandedIn_WithNoConnectorTransaction()
    {
        // Independent review of #160: LoadFamily, EditScope.Commit and Export commit THEIR OWN transactions
        // into the run's group between blocks. CommittedCount never sees them, so the group looked empty
        // and was rolled back -- silently undoing the work while reporting success. The executor forwards
        // every DocumentChanged it observes; one for this document is enough to make the group real.
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("ambient", journal);
        set.Open(document, isAmbient: true);
        journal.Clear();

        set.NoteDocumentChanged(document.DocumentId);
        var result = set.CommitAll();

        Assert.True(result.Success);
        Assert.Contains("ambient:group.Assimilate", journal);
        Assert.DoesNotContain("ambient:group.RollBack", journal);
        Assert.Equal(new[] { "ambient (active document)" }, result.CommittedDocuments);
    }

    [Fact]
    public void NoteDocumentChanged_ForADocumentThisRunDoesNotManage_IsIgnored()
    {
        var journal = new List<string>();
        var set = NewSet();
        set.Open(new JournalingDocumentAdapter("ambient", journal), isAmbient: true);
        journal.Clear();

        set.NoteDocumentChanged("tmp-somebody-else");
        var result = set.CommitAll();

        Assert.True(result.Success);
        Assert.Equal(new[] { "ambient:group.RollBack", "ambient:group.Dispose" }, journal);
    }

    [Fact]
    public void Settle_KeepAfterAnObservedExternalCommit_Assimilates()
    {
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("ambient", journal);
        set.Open(document, isAmbient: true);
        journal.Clear();

        set.NoteDocumentChanged(document.DocumentId);
        set.SettleCore(document, keep: true);

        Assert.Equal(new[] { "ambient:group.Assimilate", "ambient:group.Dispose" }, journal);
    }

    [Fact]
    public void WithTransaction_AdoptingADocument_UnwindsTheGroupWhenTheTransactionCannotStart()
    {
        // Independent review of #160 (finding 13): the adoption path opened a group and then started the
        // block's transaction with no unwind between them, so a transaction that failed to start left a
        // group registered that nothing asked for -- the document stayed adopted for the rest of the run
        // and a retry could never get a clean group.
        var set = NewSet();
        var document = new FakeDocumentAdapter { DocumentId = "tmp-adopted", TransactionThrowOnStart = true };

        Assert.ThrowsAny<Exception>(() => set.RunWithTransactionCore(document, () => { }));

        Assert.Equal(0, set.Count);
        Assert.Contains("RollBack", document.LastTransactionGroup!.Calls);
        Assert.Contains("Dispose", document.LastTransactionGroup!.Calls);
    }

    [Fact]
    public void CommitAll_AssimilatesAGroupSomethingCommittedInto_EvenAnEmptyBlock()
    {
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("ambient", journal);
        set.Open(document, isAmbient: true);
        set.RunWithTransactionCore(document, () => { });
        journal.Clear();

        set.CommitAll();

        Assert.Equal(new[] { "ambient:group.Assimilate", "ambient:group.Dispose" }, journal);
    }

    [Fact]
    public void CommitAll_ABlockThatThrew_LeavesTheGroupEmpty_AndItIsRolledBack()
    {
        // A rolled-back block committed nothing, so the run's group is still empty at the end: no undo
        // entry for a run whose only write failed and was caught.
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("ambient", journal);
        set.Open(document, isAmbient: true);
        Record.Exception(() => set.RunWithTransactionCore(document, () => throw new InvalidOperationException("nope")));
        journal.Clear();

        set.CommitAll();

        Assert.Equal(new[] { "ambient:group.RollBack", "ambient:group.Dispose" }, journal);
    }

    [Fact]
    public void Settle_KeepOnAnEmptyGroup_RollsItBack_ReportedAsKept()
    {
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("scratch", journal);
        set.OpenAdoptedForTesting(document);
        journal.Clear();

        set.SettleCore(document, keep: true);

        Assert.Equal(new[] { "scratch:group.RollBack", "scratch:group.Dispose" }, journal);
        Assert.True(Assert.Single(set.Settlements).Kept);
    }

    [Fact]
    public void Settle_KeepAfterAWrite_Assimilates()
    {
        var journal = new List<string>();
        var set = NewSet();
        var document = new JournalingDocumentAdapter("scratch", journal);
        set.OpenAdoptedForTesting(document);
        set.RunWithTransactionCore(document, () => { });
        journal.Clear();

        set.SettleCore(document, keep: true);

        Assert.Equal(new[] { "scratch:group.Assimilate", "scratch:group.Dispose" }, journal);
    }

    [Fact]
    public void WithTransaction_OnADocumentThisRunHasNotTouched_AdoptsItWithAGroup_AndKeepsTheGroupAfterTheBlock()
    {
        // The route that replaced WithTransaction-adoption: a document a prior call created and left open is reached
        // through Application.Documents and written to inside a block; the connector opens a group for it,
        // the block's transaction inside that, and the group stays for the rest of the run.
        var journal = new List<string>();
        var set = NewSet();
        var ambient = new JournalingDocumentAdapter("ambient", journal);
        var other = new JournalingDocumentAdapter("other", journal);
        set.Open(ambient, isAmbient: true);
        journal.Clear();

        set.RunWithTransactionCore(other, () => journal.Add("WRITE"));

        Assert.Equal(
            new[] { "other:group.Start", "other:tx.Start", "WRITE", "other:tx.Commit", "other:tx.Dispose" },
            journal);
        Assert.Equal(2, set.Count);
        Assert.Equal(ManagedDocumentTransactions.DocumentOrigin.AdoptedExisting, set.OriginForTesting(other.DocumentId));
    }
}
