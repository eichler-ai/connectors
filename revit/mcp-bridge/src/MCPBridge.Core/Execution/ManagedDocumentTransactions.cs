using System;
using System.Collections.Generic;
using System.Linq;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Every document one script run may write to, each with the Transaction/TransactionGroup pair this
/// connector opened and owns for it (issue #24) -- the ambient document, any document this run creates
/// via CreateProjectDocument/CreateFamilyDocument, and (a later addition, same mechanism) any
/// PRE-EXISTING document this run opens via ScriptGlobals.OpenForWriting -- e.g. one a PRIOR
/// execute_script call created and left open, addressed again by Title (PRD §14: no document_id).
///
/// WHY N PAIRS AND NOT ONE. TransactionScriptExecutor used to manage exactly one pair, around the
/// ambient (active) document. Revit's one-open-transaction rule is per-DOCUMENT, not global, so a
/// document a script creates mid-run is simply not covered by that pair -- writing to it threw
/// ModificationOutsideTransactionException, and the script could not open its own Transaction either,
/// because ScriptApiDenylist check 1 refuses that unconditionally. Rather than teaching the denylist
/// which document a Transaction targets (assessed and rejected: Revit hands back different wrapper
/// objects for "the same" document depending on the API entry point, and DocumentIdentity is weakest
/// for exactly the unsaved documents this is about), the connector opens the transaction itself, in
/// the same step that creates the document. The denylist rule stays completely unconditional and gains
/// no new bypass surface; if this plumbing has a bug the failure mode is a broken FEATURE, not a
/// broken security boundary.
///
/// A SCRIPT NEVER COMMITS OR ROLLS BACK ANYTHING, with N documents exactly as with one. Every commit
/// happens here, after the script's C# has already finished running, and the script's own
/// return-or-throw governs all N uniformly: it returned normally, so commit everything; it threw or
/// was cancelled, so roll everything back. There is no per-document success concept for a script to
/// express, and none is invented.
///
/// COMMIT ORDER IS DELIBERATE: created documents first, in creation order, and the AMBIENT document
/// last. A committed Revit transaction cannot be un-committed, so ordering is the only lever over what
/// a partial failure can damage -- and committing the ambient document last means any failure among
/// the created ones is still answerable by rolling the ambient one back. The ambient document is a
/// real model a human has open; a created one is unsaved and in-memory, touching no file, no central
/// model and no session, so confining partial-commit fallout to those is strictly the better trade.
/// See <see cref="ManagedDocumentCommitResult"/> for how what did land is reported (PRD §01).
///
/// INTERNAL, AND THAT IS A SECURITY BOUNDARY, NOT A STYLE CHOICE. RoslynScriptRunner.LoadableReferences()
/// references every assembly loaded in the Revit AppDomain -- MCPBridge.Core included -- so any PUBLIC
/// type here is a type an untrusted agent script can name and construct. Live-verified against real Revit
/// 2027 while this class was public: a script constructed `new MCPBridge.RevitAdapter.RevitDocumentAdapter(raw)`
/// on a document it had made itself, called CreateTransaction on it, wrote a Level and COMMITTED it --
/// a real, unmanaged transaction ScriptApiDenylist never saw, because the `new Transaction(...)` happens
/// inside adapter code rather than in the script's own syntax tree, which is all the AST walk examines.
/// This class was the same shape and strictly more powerful (Open/CreateAndOpen*/CommitAll/RollBackAll on
/// documents the executor owns). `internal` closes it structurally -- a script cannot name what it cannot
/// see -- which is why it was preferred over adding our own types to the denylist's tables: those are
/// meant to stay small and Revit-API-focused, and a list of forbidden names is only ever as complete as
/// the last person to remember to extend it. Reflection can still reach this, exactly as ScriptApiDenylist
/// documents for its own entries; that is the accepted GUARD-not-sandbox line, unchanged.
/// </summary>
internal sealed class ManagedDocumentTransactions
{
    /// <summary>
    /// Independent PR review finding: a plain <c>bool IsAmbient</c> conflated two genuinely different
    /// kinds of "not ambient" document once <c>OpenForWriting</c> shipped. Before it, every non-ambient
    /// entry was a document THIS run created (unsaved, in-memory, touching no file/central model/session)
    /// -- the entire justification <see cref="CommitAll"/>'s "created first, ambient last" ordering and
    /// <see cref="TransactionScriptExecutor"/>'s partial-commit remedy text both give for why confining
    /// partial-failure fallout to non-ambient documents is safe. <c>OpenForWriting</c> can adopt a
    /// PRE-EXISTING document that is just as real as the ambient one -- a saved, possibly workshared model
    /// that merely isn't the active document -- and a bare bool would have silently let that document
    /// commit FIRST, alongside genuinely-throwaway created ones, exposing it to exactly the fallout the
    /// ordering exists to avoid. Three states, ordered by how safe committing them early is.
    /// </summary>
    internal enum DocumentOrigin
    {
        /// <summary>A document THIS run created via CreateProjectDocument/CreateFamilyDocument -- unsaved, in-memory, safest to commit first.</summary>
        CreatedThisRun,

        /// <summary>A PRE-EXISTING document THIS run adopted via OpenForWriting -- may be a real, saved model; committed after created documents but still before the ambient one.</summary>
        AdoptedExisting,

        /// <summary>The run's active document -- always committed last (see this class's own doc comment).</summary>
        Ambient,
    }

    private sealed class Entry
    {
        public Entry(IDocumentAdapter document, ITransactionGroupAdapter group, ITransactionAdapter transaction, DocumentOrigin origin)
        {
            Document = document;
            Group = group;
            Transaction = transaction;
            Origin = origin;
        }

        public IDocumentAdapter Document { get; }
        public ITransactionGroupAdapter Group { get; }

        /// <summary>
        /// NULL WHILE NO TRANSACTION IS OPEN ON THIS DOCUMENT (issue #132). Under `always-open` this was
        /// readonly and never null, because the pair opened and closed together. `settle-on-request` lets
        /// a script close the transaction and keep the group -- the state where Document.IsModifiable is
        /// false but the rollback boundary still holds -- so every reader here must cope with its absence
        /// rather than assume the pair. That is the whole mechanism, expressed in one field.
        /// </summary>
        public ITransactionAdapter? Transaction { get; set; }

        public DocumentOrigin Origin { get; }

        /// <summary>
        /// Failures accumulated across EVERY transaction commit on this document, not just the last
        /// (issue #132; found by review). ITransactionAdapter.CommitFailures is overwritten on each
        /// Commit(), so once a document can commit N times in one run, reading it only at end-of-run
        /// destroys the first N-1 failure sets -- and it is the only channel carrying the reason for a
        /// Revit-forced ProceedWithRollBack. Appended at every commit this class performs.
        /// </summary>
        public List<FailureSummary> AccumulatedFailures { get; } = new();

        /// <summary>
        /// True while this document is inside a Connector.WithoutTransaction scope. Backs the
        /// same-document re-entry refusal (issue #132, decision 3): nesting is allowed across DIFFERENT
        /// documents and refused on the same one, which is the identical rule the nested-WithTransaction
        /// refusal states, so there is one concept to learn rather than two.
        /// </summary>
        public bool InWithoutTransactionScope { get; set; }

        /// <summary>
        /// How this document is named in a partial-commit report. Title, because that is also how a
        /// created document is addressed across execute_script calls (PRD §14: it stays in
        /// Application.Documents and is found there by Title -- there is no document_id for it), so the
        /// name in the report is one an agent can act on.
        /// </summary>
        public string Describe() => Origin switch
        {
            DocumentOrigin.Ambient => $"{Document.Title} (active document)",
            DocumentOrigin.AdoptedExisting => $"{Document.Title} (adopted via OpenForWriting)",
            _ => Document.Title,
        };
    }

    private readonly string _transactionName;
    private readonly IUiApplicationAdapter _uiApplication;
    private readonly List<Entry> _entries = new();

    public ManagedDocumentTransactions(string transactionName, IUiApplicationAdapter uiApplication)
    {
        _transactionName = transactionName;
        _uiApplication = uiApplication;
    }

    /// <summary>How many documents are currently under management, ambient included.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Opens and starts a TransactionGroup + Transaction for <paramref name="document"/> and tracks
    /// them. <paramref name="isAmbient"/> marks the run's active document -- at most one, opened by
    /// TransactionScriptExecutor before the script runs; created documents are opened lazily as the
    /// script creates them (via the internal <see cref="DocumentOrigin"/> overload below, which
    /// distinguishes them from a document OpenForWriting adopted -- see that enum's own doc comment).
    ///
    /// Guards against opening the SAME document twice, by <see cref="IDocumentAdapter.DocumentId"/>.
    ///
    /// KNOWN, ACCEPTED GAP, DOCUMENTED RATHER THAN SILENTLY LEFT: DocumentId is cached in
    /// DocumentIdentity's process-lifetime table (MCPBridge.RevitAdapter/DocumentIdentity.cs), keyed on
    /// the live Document reference. For an UNSAVED document -- the primary case here, since a document a
    /// prior call created is unsaved by construction -- resolution mints a fresh `tmp-&lt;guid&gt;` on
    /// every cache MISS, so two independently-obtained adapters for the SAME unsaved document can
    /// legitimately get different ids if Revit hands back different wrapper objects for either lookup
    /// (the same "different wrappers per API entry point" gotcha this file already documents elsewhere).
    /// A second, independent PR review round proposed a reference-equality backstop for exactly this case
    /// and then found it PROVABLY DEAD: any backstop keyed on the Document reference (directly, or via
    /// ReferenceEquals) hits the identical cache-miss problem DocumentId already has, since both are keyed
    /// on the same reference -- there is no comparison two DIFFERENT Document wrapper objects for the same
    /// live document can pass that DocumentId does not already catch on its own. Closing this gap for real
    /// needs a comparison on something that stays stable ACROSS wrapper instances (e.g. a value read off
    /// the document itself, not the wrapper), which needs live-Revit verification before it's added here --
    /// not done as part of this fix. Until then: OpenForWriting on a document reached through a DIFFERENT
    /// API entry point than however it's already tracked (e.g. re-found via `Application.Documents` when
    /// it was originally opened as the ambient document) can silently open a SECOND transaction on it
    /// rather than being refused -- Revit's own one-open-transaction-per-document rule is what actually
    /// surfaces that case, as a raw exception rather than this guard's signposted one.
    ///
    /// KNOWN, ACCEPTED gap in the other direction: a workshared document's DocumentId is derived from its
    /// CentralModelPath, so a local copy and its own central model opened in the same session legitimately
    /// share one DocumentId -- OpenForWriting on the second would be refused as a false-positive "already
    /// open". Not worth restructuring for; noted so a future reader doesn't have to rediscover it live.
    ///
    /// This guard was never needed before OpenForWriting shipped: CreateProjectDocument/CreateFamilyDocument
    /// only ever hand back a document that didn't exist until that call returned -- nothing else could
    /// already reference it -- but OpenForWriting specifically targets a document that MAY already be
    /// tracked (the ambient one, or one opened earlier this same run), which a second Transaction.Start()
    /// on the same document cannot safely do (Revit allows only one open Transaction per document at a
    /// time) and which CommitAll/RollBackAll were never written to iterate twice for one document. Fails
    /// fast, before any TransactionGroup/Transaction is created for the duplicate -- no wasted allocation.
    /// </summary>
    public void Open(IDocumentAdapter document, bool isAmbient = false) =>
        Open(document, isAmbient ? DocumentOrigin.Ambient : DocumentOrigin.CreatedThisRun);

    /// <summary>
    /// Test-only entry point for the <see cref="DocumentOrigin.AdoptedExisting"/> tier (independent PR
    /// review finding: that tier previously had ZERO tier-1 coverage at all -- not the ordering, not
    /// <see cref="Entry.Describe"/>'s "(adopted via OpenForWriting)" branch, not the
    /// <c>AnyCommittedDocumentMayBeReal</c> true branch -- because the only way to reach it was through
    /// <see cref="OpenExisting"/>, which needs a real <c>Autodesk.Revit.DB.Document</c>. This lets a fake
    /// exercise it directly, the same way <see cref="RequireExistingDocumentSource"/> was split out for
    /// the same reason.
    /// </summary>
    internal void OpenAdoptedForTesting(IDocumentAdapter document) => Open(document, DocumentOrigin.AdoptedExisting);

    private void Open(IDocumentAdapter document, DocumentOrigin origin)
    {
        var existing = _entries.FirstOrDefault(e => e.Document.DocumentId == document.DocumentId);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                $"A managed transaction is already open for '{SafeDescribe(existing)}' (DocumentId=" +
                $"{document.DocumentId}) -- Open was called twice for the same document. This happens " +
                "when OpenForWriting is called on the ambient document, or on a document already opened " +
                "via CreateProjectDocument/CreateFamilyDocument/OpenForWriting earlier in this same run.");
        }

        var group = document.CreateTransactionGroup(_transactionName);
        try
        {
            group.Start();
        }
        catch
        {
            // group.Start() itself threw (PR review): nothing tracks this adapter and nothing else
            // will ever dispose it -- same only-terminal-point reasoning as the catch below.
            SafeDispose(null, group);
            throw;
        }

        ITransactionAdapter? transaction = null;
        try
        {
            transaction = document.CreateTransaction(_transactionName);
            transaction.Start();
        }
        catch
        {
            // The group started but the transaction did not, so nothing tracks the group and nothing
            // would ever close it. Undo it here rather than leaking an open group into the session --
            // and dispose both adapters (issue #34): nothing will ever track them, so this catch is
            // their only terminal point.
            SafeRollBack(group.RollBack);
            SafeDispose(transaction, group);
            throw;
        }

        _entries.Add(new Entry(document, group, transaction, origin));
        _originHistory[document.DocumentId] = origin;

        // #122: capture a created document's identity NOW -- while the entry exists and the document is
        // freshly in hand -- into a list that outlives the entry set, so the executor can report it on
        // every run even after CommitAll/RollBackAll has dropped the entry. Mirrors _settlements.
        // Title is read best-effort (a live Revit call that can throw for a document mid-transition -- the
        // same reason Describe routes through SafeDescribe); DocumentId is safe, it was just read above.
        if (origin == DocumentOrigin.CreatedThisRun)
        {
            string title;
            try
            {
                title = document.Title;
            }
            catch
            {
                title = "(title unavailable -- match by document_id)";
            }

            _createdDocuments.Add(new CreatedDocumentRecord(title, document.DocumentId));
        }
    }

    /// <summary>
    /// Creates a new project document and opens its managed transaction in one step -- the whole point
    /// of issue #24's chosen approach. See <see cref="IDocumentCreationSource.CreateProjectDocument"/>
    /// for template resolution.
    /// </summary>
    public IDocumentAdapter CreateAndOpenProjectDocument(string? templatePath) =>
        CreateAndOpen(source => source.CreateProjectDocument(templatePath));

    /// <summary>Family-document counterpart of <see cref="CreateAndOpenProjectDocument"/>.</summary>
    public IDocumentAdapter CreateAndOpenFamilyDocument(string templatePath) =>
        CreateAndOpen(source => source.CreateFamilyDocument(templatePath));

    private IDocumentAdapter CreateAndOpen(Func<IDocumentCreationSource, IDocumentAdapter> create)
    {
        var source = _uiApplication as IDocumentCreationSource
            ?? throw new NotSupportedException(
                $"creating a document needs a live Revit session, but {_uiApplication.GetType().Name} does not " +
                $"implement {nameof(IDocumentCreationSource)}. Only the live adapter does -- " +
                "Autodesk.Revit.ApplicationServices.Application is non-constructible outside a running " +
                "Revit session, so a fake genuinely cannot create a document. A test that needs to create " +
                "one belongs in the tier-2 live harness (revit/test-harness), not MCPBridge.Core.Tests.");

        var created = create(source);
        Open(created);
        return created;
    }

    /// <summary>
    /// Opens a managed transaction on a document that already exists -- the adapter half of
    /// ScriptGlobals.OpenForWriting. Unlike <see cref="CreateAndOpen"/>, this document is not new: Open's
    /// own DocumentId guard is what actually protects against double-tracking it (the ambient document,
    /// or one already opened via CreateProjectDocument/CreateFamilyDocument/OpenForWriting this run).
    /// Tracked as <see cref="DocumentOrigin.AdoptedExisting"/>, not <see cref="DocumentOrigin.CreatedThisRun"/>
    /// -- see that enum's own doc comment for why the distinction matters to commit ordering.
    /// </summary>
    public IDocumentAdapter OpenExisting(Autodesk.Revit.DB.Document rawDocument)
    {
        // Independent PR review finding: a script calling OpenForWriting(null) -- easy, since the
        // standard by-Title lookup preamble leaves `doc` null when nothing matches and a script forgets
        // the null check -- used to reach RevitDocumentAdapter's constructor and fail deep inside
        // DocumentIdentity's ConditionalWeakTable with a bare ArgumentNullException naming "key", not
        // "document". Signposted here instead, per PRD §01 (Raw<T>'s own doc comment names this exact
        // class of unexplained-exception as the thing to avoid).
        if (rawDocument is null)
        {
            throw new ArgumentNullException(nameof(rawDocument),
                "OpenForWriting was called with a null document -- likely a by-Title lookup that found no " +
                "match and wasn't null-checked before being passed in.");
        }

        var source = RequireExistingDocumentSource();
        var adapter = source.WrapExisting(rawDocument);

        // The remembered tier wins when this run has seen the document before -- the same rule
        // RunWithTransactionCore applies, and this is the route the docs actually send people down
        // ("one this run already Settled is the exception, and works again"). Hardcoding
        // AdoptedExisting here re-tiered a settled AMBIENT document as adopted, so the human's real
        // open model could commit before another saved model and would be named "(adopted via
        // OpenForWriting)" in a partial-commit notice. Fixing only the other call site left the
        // documented path broken.
        Open(adapter, OriginFor(adapter.DocumentId, DocumentOrigin.AdoptedExisting));
        return adapter;
    }

    /// <summary>
    /// Split out from <see cref="OpenExisting"/> specifically so the "does this run's adapter support
    /// OpenForWriting at all" check is tier-1 testable on its own (independent PR review finding: the
    /// combined method was untestable in its ENTIRETY at tier 1, when only the raw-Document-wrapping half
    /// genuinely needs live Revit -- mirrors <see cref="CreateAndOpen"/>'s own existing shape, which keeps
    /// the same cast-and-throw check separable from its Revit-typed template-path argument). A test needs
    /// no Autodesk.Revit.DB.Document reference to call this -- only an adapter that does or doesn't
    /// implement IExistingDocumentSource, which every existing tier-1 fake already is (the "doesn't"
    /// case).
    /// </summary>
    internal IExistingDocumentSource RequireExistingDocumentSource() =>
        _uiApplication as IExistingDocumentSource
        ?? throw new NotSupportedException(
            $"OpenForWriting needs a live Revit session, but {_uiApplication.GetType().Name} does not " +
            $"implement {nameof(IExistingDocumentSource)}. Only the live adapter does -- " +
            "Autodesk.Revit.DB.Document is non-constructible/non-wrappable outside a running Revit " +
            "session, so a fake genuinely cannot supply one. A test that needs this belongs in the " +
            "tier-2 live harness (revit/test-harness), not MCPBridge.Core.Tests.");

    /// <summary>
    /// Records one Connector.Settle call, so TransactionScriptExecutor can raise the notice PRD §06
    /// requires (issue #132, decision 2). Only Settle is recorded: it is the irreversible one -- it
    /// makes prior writes permanent or discards them, and settling the AMBIENT document gives up the
    /// rollback remedy for a human's real open model. WithTransaction/WithoutTransaction stay silent,
    /// because the group still covers rollback and nothing irreversible has happened; a notice per
    /// scope would bury the real ones under any script that writes in a loop.
    /// </summary>
    internal readonly struct SettlementRecord
    {
        public SettlementRecord(string document, bool kept)
        {
            Document = document;
            Kept = kept;
        }

        public string Document { get; }

        /// <summary>True for Settle(keep: true) -- assimilated, so the work is permanent. False for a rollback.</summary>
        public bool Kept { get; }
    }

    private readonly List<SettlementRecord> _settlements = new();

    /// <summary>
    /// Failures accumulated by documents that have since been SETTLED and deregistered. Without this they
    /// vanished: <see cref="Entry.AccumulatedFailures"/> exists precisely so a document committing N times
    /// keeps all N failure sets, but <see cref="CommitAll"/> is its only reader and walks <c>_entries</c> --
    /// which a settled document has left. Every Revit warning it raised, including ones from earlier
    /// WithTransaction blocks, silently never reached notices[] (PRD §07/§01). Error-severity failures
    /// survived only incidentally, because CloseTransaction throws and Settle wraps the message.
    /// </summary>
    private readonly List<FailureSummary> _settledFailures = new();

    /// <summary>
    /// The tier each document was FIRST opened under, kept for the life of the run so a settle-then-write
    /// cycle cannot change it. Without this, re-opening after a settle guessed
    /// <see cref="DocumentOrigin.AdoptedExisting"/> -- so a settled AMBIENT document would come back
    /// tiered as adopted and stop committing LAST, silently inverting the one ordering guarantee
    /// <see cref="CommitAll"/> exists to provide: that a failure among the other documents is still
    /// answerable by rolling the human's real open model back.
    /// </summary>
    private readonly Dictionary<string, DocumentOrigin> _originHistory = new();

    /// <summary>Every Settle performed this run, in order. Read by the executor after the script finishes.</summary>
    public IReadOnlyList<SettlementRecord> Settlements => _settlements;

    /// <summary>
    /// See <see cref="_settledFailures"/>. Read by the executor on the FAILED path, where CommitAll never
    /// runs and would otherwise be the only thing that surfaced them.
    /// </summary>
    public IReadOnlyList<FailureSummary> SettledFailures => _settledFailures;

    /// <summary>#122: one per document THIS run created (CreateProjectDocument/CreateFamilyDocument), by
    /// Title and the tmp- document_id that lets a later call find it.</summary>
    internal readonly struct CreatedDocumentRecord
    {
        public CreatedDocumentRecord(string title, string documentId)
        {
            Title = title;
            DocumentId = documentId;
        }

        public string Title { get; }

        /// <summary>The tmp- id of the unsaved document, the handle a follow-up execute_script targets to close/save it.</summary>
        public string DocumentId { get; }
    }

    private readonly List<CreatedDocumentRecord> _createdDocuments = new();

    /// <summary>
    /// #122: the documents THIS run created, captured by identity AT creation so they survive
    /// <see cref="CommitAll"/>/<see cref="RollBackAll"/> dropping the entry set -- the same reason
    /// <see cref="_settlements"/> is its own list. Read by the executor to report them on EVERY run: a
    /// created document outlives its run (rollback undoes content, not existence), so an agent whose
    /// script threw after creating documents would otherwise be left holding an error and no handle to
    /// what it made (split from #114). Only <see cref="DocumentOrigin.CreatedThisRun"/> is captured --
    /// the ambient and OpenForWriting-adopted documents existed before the run and are not orphaned by it.
    /// </summary>
    public IReadOnlyList<CreatedDocumentRecord> CreatedDocuments => _createdDocuments;

    /// <summary>
    /// Runs <paramref name="body"/> with this document NOT modifiable: the connector commits and closes
    /// its transaction, keeps the TransactionGroup, and opens a fresh transaction in that same group
    /// afterwards (issue #132). The group is what preserves all-or-nothing rollback across the gap --
    /// verified live: TransactionGroup.RollBack() discards transactions already committed inside it.
    ///
    /// A document with no managed transaction is already non-modifiable, so the body simply runs; that
    /// is the honest no-op rather than a refusal the script could do nothing about. Re-entry on the SAME
    /// document is refused (decision 3); nesting across DIFFERENT documents is allowed, which is how
    /// LoadFamily -- needing both source and target non-modifiable -- is expressed.
    /// </summary>
    internal void RunWithoutTransactionCore(IDocumentAdapter document, Action body)
    {
        var entry = FindEntry(document);
        if (entry is null)
        {
            body();
            return;
        }

        if (entry.InWithoutTransactionScope)
        {
            throw new InvalidOperationException(
                $"WithoutTransaction is already open for '{SafeDescribe(entry)}' -- it cannot be nested on the " +
                "same document. Nesting across DIFFERENT documents is allowed and is how a call needing two " +
                "non-modifiable documents (Document.LoadFamily) is written.");
        }

        entry.InWithoutTransactionScope = true;
        try
        {
            CloseTransaction(entry);
            body();
        }
        finally
        {
            entry.InWithoutTransactionScope = false;

            // Reopened even when the body threw, because a script may CATCH its own exception and carry
            // on writing -- leaving the document silently non-modifiable would fail its next write for a
            // reason two steps from the cause.
            //
            // SWALLOWED, uniquely in this file, and only on the throwing path: an exception raised inside
            // a `finally` REPLACES the in-flight one, so a reopen that failed here would destroy the
            // script's real error and report a transaction-start failure in its place. That is the §01
            // sin this whole codebase is written against -- an agent handed an error that is not the one
            // that happened. If the reopen does fail, the document stays non-modifiable and the next
            // write fails loudly with `script-write-outside-transaction`, which is a legible outcome; a
            // masked root cause is not. On the SUCCESS path a reopen failure still propagates normally,
            // because there is no exception to destroy.
            SafeReopenAfterScope(entry);
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> with a transaction the CONNECTOR opens and commits around it (issue
    /// #132) -- the counterpart that makes an edit scope writable, since StairsEditScope needs no
    /// transaction to start and a transaction to write, and its Commit() then needs that transaction
    /// CLOSED again. Closing at block end is therefore load-bearing, not tidiness.
    ///
    /// Nesting on the same document is REFUSED (decision 1) rather than joined transparently: joining
    /// would make "the connector commits at block end" false for the inner block and would let a caught
    /// inner failure ride silently on the outer commit. The refusal is loud and can be relaxed later;
    /// withdrawing a silent join could not be.
    /// </summary>
    internal void RunWithTransactionCore(IDocumentAdapter document, Action body)
    {
        var entry = FindEntry(document);
        if (entry is null)
        {
            // No managed pair yet -- typically a document settled earlier this run, or one the script
            // never wrote to. Open a fresh group+transaction for it; end-of-run assimilate covers it
            // like any other managed document.
            //
            // The tier comes from _originHistory when this run has seen the document before, NOT from a
            // fresh guess: a settled ambient document re-opened as AdoptedExisting would stop committing
            // last, which is the ordering guarantee CommitAll exists to provide.
            Open(document, OriginFor(document.DocumentId, DocumentOrigin.AdoptedExisting));
            RunBody(FindEntry(document)!, body, openedGroupHere: true);
            return;
        }

        if (entry.Transaction is not null)
        {
            throw new InvalidOperationException(
                $"A transaction is already open for '{SafeDescribe(entry)}' -- WithTransaction cannot be " +
                "nested on the same document. Write directly instead: the enclosing scope's transaction " +
                "already covers this document. (Revit allows only one open transaction per document, and " +
                "the connector refuses here rather than letting Revit refuse with less context.)");
        }

        ReopenTransaction(entry);
        RunBody(entry, body, openedGroupHere: false);
    }

    /// <summary>
    /// Runs a WithTransaction body and closes its transaction, unwinding THIS BLOCK if the body throws.
    ///
    /// THE UNWIND IS NOT REDUNDANT WITH RollBackAll, and an earlier version that relied on it was wrong
    /// in two ways -- both reachable only when the SCRIPT CATCHES, which is ordinary code ("try this API,
    /// fall back if it fails"), not an exotic case:
    ///
    /// 1. WEDGE. Leaving the transaction open made every later WithTransaction on that document throw
    ///    "a transaction is already open" for the rest of the run, with Settle(discard) -- which throws
    ///    the work away -- as the only escape. The sibling WithoutTransaction recovers cleanly from the
    ///    identical mistake, so the asymmetry was an oversight, not a design.
    /// 2. SILENT COMMIT OF FAILED WORK. The partial writes of a body that threw stayed in the open
    ///    transaction, and CommitAll (or a later Settle(keep: true)) then made them permanent -- while
    ///    Connector's own summary promises the connector "commits when the block ends". Neither
    ///    committing nor rolling back was the one behaviour nothing documented.
    ///
    /// When this call also OPENED the group (the no-entry path), the group is unwound and deregistered
    /// too: nothing was committed into it, so leaving it open would strand a group the script never asked
    /// for, and deregistering lets a retry open a clean one.
    /// </summary>
    private void RunBody(Entry entry, Action body, bool openedGroupHere)
    {
        try
        {
            body();
        }
        catch
        {
            var transaction = entry.Transaction;
            if (transaction is not null)
            {
                SafeRollBack(transaction.RollBack);
                entry.Transaction = null;
                SafeDispose(transaction, null);
            }

            // MEMBERSHIP RE-CHECKED, not assumed from openedGroupHere: the body may have SETTLED this
            // document, which assimilates, disposes and deregisters it. Rolling back then would be
            // RollBack() on an assimilated group -- the invalid case SettleCore's own comment names --
            // on an already-disposed handle. The Safe* wrappers would swallow it, so the symptom is a
            // defeated invariant rather than a crash, which is worse.
            if (openedGroupHere && _entries.Contains(entry))
            {
                SafeRollBack(entry.Group.RollBack);
                SafeDispose(null, entry.Group);
                _entries.Remove(entry);
            }

            throw;
        }

        CloseTransaction(entry);
    }

    /// <summary>
    /// Settles this document's group so Revit will allow Close/Save/SaveAs/SynchronizeWithCentral, which
    /// refuse while ANY transaction or transaction GROUP is open -- verified live, and unlike the
    /// EditScope case the group really is the bar here.
    ///
    /// <paramref name="keep"/> true assimilates (the work becomes permanent, retroactively, for this
    /// document) and false rolls the group back (the work is discarded). The DIRECTION IS THE SCRIPT'S TO
    /// STATE, never inferred: the connector cannot see doc.Save()/doc.Close() at all -- the denylist is a
    /// compile-time walk that gates but cannot intercept, and neither DocumentSavingAs nor DocumentClosing
    /// fires while a group is open, because Revit's transaction-phase check precedes event dispatch.
    ///
    /// The entry is DEREGISTERED, which is what keeps CommitAll/RollBackAll correct: they walk every entry
    /// and call Commit/Assimilate/RollBack plus Dispose, so a settled pair left in the set would be
    /// operated on a second time -- RollBack() on an assimilated group being exactly the invalid case
    /// those methods warn about. Deregistering also frees a later WithTransaction to open a FRESH group
    /// for this document, since Open's DocumentId guard only refuses while an entry exists.
    /// </summary>
    internal void SettleCore(IDocumentAdapter document, bool keep)
    {
        var entry = FindEntry(document);
        if (entry is null)
        {
            throw new InvalidOperationException(
                $"Settle was called for a document this run does not manage (DocumentId={document.DocumentId}). " +
                "Nothing is open for it, so Close/Save/SaveAs are already permitted -- and a document settled " +
                "earlier in this same run is no longer managed either.");
        }

        if (entry.InWithoutTransactionScope)
        {
            throw new InvalidOperationException(
                $"Settle cannot run inside a WithoutTransaction scope for '{SafeDescribe(entry)}' -- that scope " +
                "reopens a transaction when it ends, which would immediately undo the settle and leave Close/Save " +
                "refused again. Settle after the scope returns.");
        }

        var description = SafeDescribe(entry);
        try
        {
            if (keep)
            {
                CloseTransaction(entry);
                entry.Group.Assimilate();
            }
            else
            {
                if (entry.Transaction is not null)
                {
                    // DISPOSED, not merely dropped (issue #34). Every other terminal path in this file
                    // pairs rollback with disposal, and Dispose is NOT implied by RollBack() nor by
                    // disposing the group -- so nulling the reference here leaked the native
                    // Autodesk.Revit.DB.Transaction back to finalizer timing, on the SUCCESS path, in the
                    // exact call skill.md tells an agent to make before closing a scratch document.
                    // Found by review; the keep:true branch was already correct because it routes through
                    // CloseTransaction, which made the asymmetry visible in two adjacent test journals
                    // and it still went unnoticed.
                    SafeRollBack(entry.Transaction.RollBack);
                    SafeDispose(entry.Transaction, null);
                    entry.Transaction = null;
                }

                entry.Group.RollBack();
            }
        }
        catch (Exception ex)
        {
            // THE ENTRY IS DELIBERATELY LEFT IN THE SET. Every other terminal path here removes it, but a
            // settle that FAILED has not reached a terminal state: the group may still be open, and the
            // executor's finally-net RollBackAll is the only thing that will close it. Removing it would
            // leak an open group into the live Revit session with nothing holding a reference -- the exact
            // failure Open's own catch was written to prevent.
            //
            // Re-thrown with context rather than raw, per §01: Revit's own message says nothing about
            // which document, which direction was attempted, or what state it is now in.
            throw new InvalidOperationException(
                $"Settling '{description}' with keep: {(keep ? "true" : "false")} failed: {ex.Message} " +
                "The document is still managed by this run, so its changes will be rolled back when the " +
                "script finishes; Close/Save/SaveAs on it will still be refused until then.", ex);
        }

        SafeDispose(null, entry.Group);
        _settledFailures.AddRange(entry.AccumulatedFailures);
        _entries.Remove(entry);
        _settlements.Add(new SettlementRecord(description, keep));
    }

    /// <summary>
    /// Raw-Document entry points for the three scopes -- the halves ScriptGlobals calls, split from the
    /// adapter-typed cores above so those stay tier-1 testable with a fake (same split, same reason, as
    /// <see cref="OpenExisting"/> and <see cref="RequireExistingDocumentSource"/>).
    ///
    /// NAMED DIFFERENTLY FROM THE CORES ON PURPOSE, and this is a compile-time trap worth knowing: an
    /// OVERLOAD SET containing both a Revit-typed and an adapter-typed parameter cannot be called at all
    /// from MCPBridge.Core.Tests. Overload resolution has to consider every candidate, so binding the
    /// adapter overload still forces Autodesk.Revit.DB.Document to resolve, and the tier-1 host -- which
    /// deliberately does not reference RevitAPI -- fails with CS0012 on the CALL SITE. Found by trying
    /// it: 12 errors across the new scope tests, none of them in code that names a Revit type. This is
    /// the same family as the type-load note on IConnectorRuntime, one rung earlier (compile rather than
    /// load), and <see cref="OpenExisting"/> only escapes it by having no adapter-typed twin.
    /// </summary>
    public void RunWithoutTransaction(Autodesk.Revit.DB.Document rawDocument, Action body) =>
        WithResolved(rawDocument, body, nameof(RunWithoutTransaction), RunWithoutTransactionCore);

    /// <summary>See <see cref="RunWithoutTransaction(Autodesk.Revit.DB.Document, Action)"/>.</summary>
    public void RunWithTransaction(Autodesk.Revit.DB.Document rawDocument, Action body) =>
        WithResolved(rawDocument, body, nameof(RunWithTransaction), RunWithTransactionCore);

    /// <summary>See <see cref="RunWithoutTransaction(Autodesk.Revit.DB.Document, Action)"/>.</summary>
    public void Settle(Autodesk.Revit.DB.Document rawDocument, bool keep)
    {
        var adapter = ResolveAdapter(rawDocument, nameof(Settle));
        SettleCore(adapter, keep);
    }

    private void WithResolved(Autodesk.Revit.DB.Document rawDocument, Action body, string memberName, Action<IDocumentAdapter, Action> run)
    {
        if (body is null)
        {
            throw new ArgumentNullException(nameof(body), $"`{memberName}` needs a body to run.");
        }

        run(ResolveAdapter(rawDocument, memberName), body);
    }

    private IDocumentAdapter ResolveAdapter(Autodesk.Revit.DB.Document rawDocument, string memberName)
    {
        // Signposted rather than left to fail deep inside DocumentIdentity's ConditionalWeakTable with a
        // bare ArgumentNullException naming "key" -- the same PRD §01 fix OpenExisting already carries,
        // and the same easy mistake (a by-Title lookup that found nothing and was not null-checked).
        if (rawDocument is null)
        {
            throw new ArgumentNullException(nameof(rawDocument),
                $"`{memberName}` was called with a null document -- likely a by-Title lookup that found no " +
                "match and was not null-checked before being passed in.");
        }

        return RequireExistingDocumentSource().WrapExisting(rawDocument);
    }

    /// <summary>
    /// The tier to re-open a document under. Shared by BOTH re-acquisition points on purpose: the fix
    /// was first applied only to RunWithTransactionCore, leaving OpenExisting -- the adapter half of
    /// Connector.OpenForWriting, and the route that member's own documentation sends people down after a
    /// settle -- still hardcoding AdoptedExisting. One rule, one place.
    /// </summary>
    private DocumentOrigin OriginFor(string documentId, DocumentOrigin fallback) =>
        _originHistory.TryGetValue(documentId, out var known) ? known : fallback;

    /// <summary>
    /// Test seam for <see cref="OriginFor"/>. OpenExisting itself needs a real Autodesk.Revit.DB.Document
    /// and so is tier-2 only; the DECISION it makes is what actually regressed, and this makes that
    /// decision assertable without one.
    /// </summary>
    internal DocumentOrigin OriginForTesting(string documentId) =>
        OriginFor(documentId, DocumentOrigin.AdoptedExisting);

    private Entry? FindEntry(IDocumentAdapter document) =>
        _entries.FirstOrDefault(e => e.Document.DocumentId == document.DocumentId);

    /// <summary>
    /// Commits and closes this entry's transaction, leaving its group open, and accumulates the Failures
    /// API result (see <see cref="Entry.AccumulatedFailures"/>). A Revit-forced rollback
    /// (ProceedWithRollBack on an error-severity failure) THROWS rather than returning quietly: today that
    /// is terminal for the run, and mid-scope it would otherwise discard the script's writes while the
    /// script kept running unaware -- the silent-loss case review flagged.
    /// </summary>
    private void CloseTransaction(Entry entry)
    {
        var transaction = entry.Transaction;
        if (transaction is null)
        {
            return;
        }

        TransactionCommitResult result;
        try
        {
            result = transaction.Commit();
        }
        finally
        {
            // Read before anything else can overwrite it, and on the throwing path too -- this is the
            // only carrier of the reason.
            AccumulateFailures(entry, transaction);
            entry.Transaction = null;
            SafeDispose(transaction, null);
        }

        if (result == TransactionCommitResult.RolledBack)
        {
            throw new InvalidOperationException(
                $"Revit rolled back the changes to '{SafeDescribe(entry)}' because a commit inside this script " +
                $"raised an error-severity failure: {LastErrorMessage(entry)}");
        }
    }

    /// <summary>
    /// <see cref="ReopenTransaction"/> that never throws -- see the call site in
    /// <see cref="RunWithoutTransactionCore"/>'s finally for why swallowing is correct there and only
    /// there: an exception from a finally block replaces the script's own, and losing the real error is
    /// worse than a document left non-modifiable, which fails legibly on the next write.
    /// </summary>
    private void SafeReopenAfterScope(Entry entry)
    {
        try
        {
            ReopenTransaction(entry);
        }
        catch
        {
            // By contract -- see the doc comment.
        }
    }

    /// <summary>Opens a fresh transaction in this entry's still-open group. No-op if one is already open.</summary>
    private void ReopenTransaction(Entry entry)
    {
        if (entry.Transaction is not null)
        {
            return;
        }

        // CreateTransaction is inside the try alongside Start(): both are calls into Revit and either
        // can fail, and the catch below has to cover both to (a) dispose whatever was created and (b)
        // give the failure the same context every spelling of it deserves.
        ITransactionAdapter? transaction = null;
        try
        {
            transaction = entry.Document.CreateTransaction(_transactionName);
            transaction.Start();
        }
        catch (Exception ex)
        {
            // Same care Open takes for the same failure (issue #34): the adapter exists, nothing tracks
            // it, and nothing else will ever dispose it -- so this catch is its only terminal point.
            // Without it the native Revit object reverts to the finalizer-timed reclamation #34 removed.
            SafeDispose(transaction, null);

            // CONTEXTUALIZED rather than re-thrown raw, per PRD §01 -- the same signposting Open,
            // ResolveAdapter and SettleCore all carry. Revit's own "the transaction could not be started"
            // names neither the document nor that this is the CONNECTOR reopening a transaction, so on the
            // WithTransaction path (where this propagates) an agent would get an error two steps from the
            // cause. On the WithoutTransaction-scope reopen path SafeReopenAfterScope swallows this, so the
            // added context is harmless there and load-bearing only here; the inner exception is preserved
            // so the raw Revit reason is never lost.
            throw new InvalidOperationException(
                $"Reopening a transaction on '{SafeDescribe(entry)}' failed: {ex.Message}", ex);
        }

        entry.Transaction = transaction;
    }

    private static void AccumulateFailures(Entry entry, ITransactionAdapter transaction)
    {
        try
        {
            entry.AccumulatedFailures.AddRange(transaction.CommitFailures);
        }
        catch (Exception ex)
        {
            entry.AccumulatedFailures.Add(new FailureSummary(
                isError: true,
                message: $"The Failures API result for '{SafeDescribe(entry)}' could not be read: {ex.Message}",
                failureDefinitionId: "mcp-bridge.commit-failures-unreadable",
                failingElementIds: Array.Empty<string>()));
        }
    }

    private static string LastErrorMessage(Entry entry) =>
        entry.AccumulatedFailures.LastOrDefault(f => f.IsError)?.Message
        ?? "the failure list naming it could not be read.";

    /// <summary>
    /// Rolls back every managed document, most recently opened first, best-effort. Used for every
    /// failed run -- a thrown script, a compile/denylist rejection, or a cancellation -- so a failure
    /// leaves no partial changes behind in ANY document, not just the ambient one.
    ///
    /// IDEMPOTENT BY DESIGN -- entries are dropped once closed, here and in <see cref="CommitAll"/>, so
    /// calling this a second time does nothing. That is what lets TransactionScriptExecutor call it
    /// unconditionally from its `finally` as a safety net: if the runner itself throws rather than
    /// returning a failed outcome, no branch has rolled anything back and every document's transaction
    /// would otherwise be left open in the live session. Self-review finding -- the single-document
    /// version had the same hole and it simply mattered less.
    /// </summary>
    public void RollBackAll()
    {
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            // SafeRollBack's success flag is deliberately discarded here, unlike in CommitAll. This path
            // runs when the whole run already failed, so there is no partial-commit report to be honest
            // WITHIN: nothing committed anywhere, and the outcome the caller returns is already the
            // script's own exception. CommitAll is the case where the distinction changes what an agent
            // is told, and that is where it is tracked.
            // Transaction may be NULL (issue #132): a WithoutTransaction scope that threw, or a
            // WithTransaction block that already committed, leaves the group open with no transaction.
            // The group is the rollback boundary, so rolling it back is what actually undoes the run --
            // the transaction half is only rolled back when one is genuinely open.
            var openTransaction = _entries[i].Transaction;
            if (openTransaction is not null)
            {
                SafeRollBack(openTransaction.RollBack);
            }

            SafeRollBack(_entries[i].Group.RollBack);

            // Issue #34: dispose strictly after this entry's terminal handling. Also the disposal
            // path for entries CommitAll deliberately left populated after an unexpected escape --
            // the executor's finally net routes them here.
            SafeDispose(openTransaction, _entries[i].Group);
        }

        _entries.Clear();
    }

    /// <summary>
    /// Commits every managed document in the order described on this class, stopping at the first
    /// failure and rolling back everything that has not already committed. Never throws: a commit-time
    /// exception is returned in the result so the caller can report it alongside what did land.
    ///
    /// Like <see cref="RollBackAll"/>, this leaves the set empty when it returns: every document has been
    /// closed one way or another, so the executor's `finally` net has nothing left to do. The one case it
    /// does NOT empty the set is an exception escaping it anyway -- see the clearing comment below, which
    /// is where that deliberate asymmetry lives.
    /// </summary>
    public ManagedDocumentCommitResult CommitAll()
    {
        // Three tiers now, not two (independent PR review finding) -- CreatedThisRun (safest: unsaved,
        // in-memory) first, AdoptedExisting (may be a real, saved model OpenForWriting adopted) next,
        // Ambient (the run's active document) always last. See DocumentOrigin's own doc comment for why
        // collapsing AdoptedExisting into the old "commit early" bucket alongside CreatedThisRun would
        // have been wrong.
        var order = _entries.Where(e => e.Origin == DocumentOrigin.CreatedThisRun)
            .Concat(_entries.Where(e => e.Origin == DocumentOrigin.AdoptedExisting))
            .Concat(_entries.Where(e => e.Origin == DocumentOrigin.Ambient))
            .ToList();

        var result = CommitInOrder(order, _settledFailures);

        // CLEARED HERE, AFTER the work, and DELIBERATELY NOT IN A `finally` (independent PR review
        // finding). Reaching this line means every document above was closed one way or another, so the
        // set must end up empty -- otherwise the executor's `finally` RollBackAll would roll back what
        // this method just committed.
        //
        // But if CommitInOrder THROWS, control never gets here and _entries stays populated ON PURPOSE.
        // The bug was clearing up front: an unexpected escape (an adapter's CommitFailures getter,
        // Document.Title inside Describe()) then left the executor's net with nothing to roll back, and
        // every not-yet-committed document leaked an open Transaction/TransactionGroup into the live
        // Revit session. A `finally` would NOT have fixed that -- it runs on the exception path too, and
        // would clear the entries just the same. Only skipping the clear on that path preserves them.
        // The reads that can actually throw are individually guarded below as well; this covers the ones
        // nobody thought of.
        _entries.Clear();
        return result;
    }

    /// <summary>
    /// <see cref="CommitAll"/>'s body, split out so the entry list is cleared only when this returns
    /// normally -- see the comment at that call site, which is the whole reason this is a separate
    /// method. Expected NOT to throw (every adapter call is guarded); the split exists precisely for the
    /// case where that expectation turns out to be wrong.
    ///
    /// SECOND-ROUND REVIEW FINDING: that "every adapter call is guarded" claim used to be FALSE here.
    /// The three list-building calls below went through <c>entry.Describe()</c> directly rather than
    /// <see cref="SafeDescribe"/> two methods away, and Describe() reads Document.Title -- a live Revit
    /// call that can throw. When it threw AFTER a successful commit it escaped
    /// TransactionScriptExecutor.ExecuteAsync (whose try has a finally but no catch) as a raw unhandled
    /// exception, destroying the very `script-partial-commit` notice that exists to report which
    /// documents committed. All three now route through SafeDescribe, which is what makes the
    /// "never throws" contract on <see cref="CommitAll"/> actually true rather than merely intended.
    /// </summary>
    private static ManagedDocumentCommitResult CommitInOrder(List<Entry> order, List<FailureSummary> settledFailures)
    {
        // SEEDED with what settled documents accumulated before they left the set -- they are gone from
        // `order`, so this is the only way their Failures-API results reach notices[].
        var failures = new List<FailureSummary>(settledFailures);
        var committed = new List<string>();
        var rolledBack = new List<string>();
        var unknownState = new List<string>();
        Exception? failure = null;
        // Independent PR review finding: PartialCommitNotice's remedy used to unconditionally claim every
        // committed document was "unsaved and in-memory" -- true when every non-ambient entry was
        // CreatedThisRun, false the moment OpenForWriting could adopt a real, saved document as
        // AdoptedExisting or the ambient one itself commits. Tracked here, at the one place that already
        // knows each entry's origin AND which of them actually committed.
        var anyCommittedDocumentMayBeReal = false;

        var index = 0;
        for (; index < order.Count; index++)
        {
            var entry = order[index];
            var attempt = AttemptCommit(entry, failures);
            if (attempt.Succeeded)
            {
                committed.Add(SafeDescribe(entry));
                if (entry.Origin != DocumentOrigin.CreatedThisRun)
                {
                    anyCommittedDocumentMayBeReal = true;
                }

                // Issue #34: terminal for this entry (committed, failures read, described) -- release
                // the native pair now instead of waiting on finalizers.
                SafeDispose(entry.Transaction, entry.Group);
                entry.Transaction = null;
                continue;
            }

            (attempt.RollbackVerified ? rolledBack : unknownState).Add(SafeDescribe(entry));
            SafeDispose(entry.Transaction, entry.Group);
            failure = attempt.Error;
            index++;
            break;
        }

        if (failure is null)
        {
            return ManagedDocumentCommitResult.Succeeded(failures, committed, anyCommittedDocumentMayBeReal);
        }

        // Whatever has not been attempted yet must not be left open: the run is a failure now, and
        // these documents have committed nothing, so rolling them back is both possible and correct.
        for (; index < order.Count; index++)
        {
            var entry = order[index];
            var openTransaction = entry.Transaction;
            var transactionUnwound = openTransaction is null || SafeRollBack(openTransaction.RollBack);
            var groupUnwound = SafeRollBack(entry.Group.RollBack);
            (transactionUnwound && groupUnwound ? rolledBack : unknownState).Add(SafeDescribe(entry));
            SafeDispose(entry.Transaction, entry.Group);
        }

        return ManagedDocumentCommitResult.Failed(failure, failures, committed, rolledBack, unknownState, anyCommittedDocumentMayBeReal);
    }

    /// <summary>
    /// What committing one document actually did. <see cref="RollbackVerified"/> is meaningful only when
    /// <see cref="Error"/> is non-null: it reports whether the best-effort unwind that followed the
    /// failure was itself observed to succeed, or threw and left this document in a state the connector
    /// cannot describe. Carried out of <see cref="AttemptCommit"/> as a value rather than a second `out`
    /// parameter so it is impossible to report a rollback the code never confirmed happened.
    /// </summary>
    private readonly struct CommitAttempt
    {
        private CommitAttempt(Exception? error, bool rollbackVerified)
        {
            Error = error;
            RollbackVerified = rollbackVerified;
        }

        public Exception? Error { get; }

        public bool RollbackVerified { get; }

        public bool Succeeded => Error is null;

        public static CommitAttempt Committed() => new(null, rollbackVerified: true);

        public static CommitAttempt Failed(Exception error, bool rollbackVerified) => new(error, rollbackVerified);
    }

    /// <summary>
    /// Commits one document's pair, mirroring exactly what the single-document version of this class
    /// did: Commit(), then Assimilate() on the group. Failure has three distinct shapes and they need
    /// different unwinding, which is why this is not a one-liner.
    ///
    /// NOTHING HERE MAY THROW -- <see cref="CommitAll"/>'s documented contract is that it never does, and
    /// the executor relies on that to report a commit failure alongside what did land instead of losing
    /// the run. Every adapter call is therefore either inside a try or routed through a Safe* helper,
    /// including the <c>CommitFailures</c> reads, which are ordinary property getters on an adapter and so
    /// can fail exactly like any other call into Revit.
    /// </summary>
    private static CommitAttempt AttemptCommit(Entry entry, List<FailureSummary> failures)
    {
        // Everything this document's own commits already raised, whether or not a transaction is still
        // open (issue #132) -- accumulated per commit rather than read once at the end, because
        // CommitFailures is overwritten on every Commit().
        failures.AddRange(entry.AccumulatedFailures);

        var transaction = entry.Transaction;
        if (transaction is null)
        {
            // No open transaction: a WithTransaction block already committed this document's work into
            // the group and closed it. Only the group remains to assimilate, which is the same terminal
            // step a committed transaction reaches below.
            try
            {
                entry.Group.Assimilate();
            }
            catch (Exception ex)
            {
                return CommitAttempt.Failed(ex, SafeRollBack(entry.Group.RollBack));
            }

            return CommitAttempt.Committed();
        }

        TransactionCommitResult result;
        try
        {
            result = transaction.Commit();
        }
        catch (Exception ex)
        {
            SafeCollectFailures(entry, failures, transaction);
            var transactionUnwound = SafeRollBack(transaction.RollBack);
            var groupUnwound = SafeRollBack(entry.Group.RollBack);
            return CommitAttempt.Failed(ex, transactionUnwound && groupUnwound);
        }

        SafeCollectFailures(entry, failures, transaction);

        if (result == TransactionCommitResult.RolledBack)
        {
            // Revit already rolled back the Transaction itself (ProceedWithRollBack) -- only the
            // TransactionGroup still needs an explicit rollback; calling Transaction.RollBack() again
            // here would be invalid. The Transaction half is therefore already undone by Revit, so the
            // group's own outcome is the whole answer for this document.
            var groupUnwound = SafeRollBack(entry.Group.RollBack);
            return CommitAttempt.Failed(new InvalidOperationException(SafeRollBackReason(entry, transaction)), groupUnwound);
        }

        try
        {
            entry.Group.Assimilate();
        }
        catch (Exception ex)
        {
            // The Transaction is already committed and Revit offers no way to un-commit it; rolling
            // the GROUP back is the only remaining lever, and it is best-effort like every other
            // rollback here. Reported as a failure either way, never silently swallowed.
            return CommitAttempt.Failed(ex, SafeRollBack(entry.Group.RollBack));
        }

        return CommitAttempt.Committed();
    }

    /// <summary>
    /// Appends this document's Failures-API summaries (PRD §07), never throwing. A getter that fails is
    /// itself reported as an error-severity failure rather than dropped -- it reaches the caller's
    /// notices[] through the same path every real Revit failure does, so PRD §01's
    /// observability-over-silence still holds for the case where the observability channel is what broke.
    /// </summary>
    private static void SafeCollectFailures(Entry entry, List<FailureSummary> failures, ITransactionAdapter transaction)
    {
        try
        {
            failures.AddRange(transaction.CommitFailures);
        }
        catch (Exception ex)
        {
            failures.Add(new FailureSummary(
                isError: true,
                message: $"The Failures API result for '{SafeDescribe(entry)}' could not be read, so any Revit " +
                         $"warnings or errors raised while committing it are not listed here: {ex.Message}",
                failureDefinitionId: "mcp-bridge.commit-failures-unreadable",
                failingElementIds: Array.Empty<string>()));
        }
    }

    /// <summary>
    /// The message for a Revit-forced rollback (ProceedWithRollBack), or a description of why that
    /// message could not be recovered. Never throws -- see <see cref="AttemptCommit"/>.
    /// </summary>
    private static string SafeRollBackReason(Entry entry, ITransactionAdapter transaction)
    {
        try
        {
            return transaction.CommitFailures.LastOrDefault(f => f.IsError)?.Message
                ?? "A transaction failure forced a rollback.";
        }
        catch (Exception ex)
        {
            return "A transaction failure forced a rollback, and the failure list naming it could not be " +
                   $"read either: {ex.Message}";
        }
    }

    /// <summary>Entry.Describe() without letting a failing Document.Title break error reporting.</summary>
    private static string SafeDescribe(Entry entry)
    {
        try
        {
            return entry.Describe();
        }
        catch
        {
            return "(a document whose title could not be read)";
        }
    }

    /// <summary>
    /// Rolls back best-effort and REPORTS WHETHER IT WORKED (independent PR review finding). Callers used
    /// to discard this, then add the document to the "rolled back" list unconditionally -- so a document
    /// whose own rollback threw was still reported as cleanly rolled back, which is precisely the claim
    /// the partial-commit report exists to get right, and the ambient document (a human's real open model)
    /// is one of the documents it could be wrong about.
    ///
    /// Still swallowing, deliberately: a rollback exception must never mask the original failure being
    /// reported, nor stop the next document's rollback. The bug was never that it caught -- it is that
    /// nobody asked what it caught.
    /// </summary>
    private static bool SafeRollBack(Action rollBack)
    {
        try
        {
            rollBack();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Disposes one entry's pair, each half independently guarded (issue #34). Called strictly AFTER
    /// the entry's terminal handling -- commit/rollback settled, CommitFailures read, SafeDescribe
    /// taken -- so a disposed native object is never subsequently touched. The adapters' own Dispose
    /// already swallows (Revit's mid-failure disposal semantics are undocumented, and a dispose
    /// failure must never mask the original failure being reported); the try/catch here additionally
    /// guards the null-conditional plumbing itself, and a dispose failure is deliberately NOT added
    /// to the §01 report: it changes nothing an agent could act on -- the object simply reverts to
    /// the pre-#34 finalizer-timed reclamation.
    /// </summary>
    private static void SafeDispose(ITransactionAdapter? transaction, ITransactionGroupAdapter? group)
    {
        try
        {
            transaction?.Dispose();
        }
        catch
        {
        }

        try
        {
            group?.Dispose();
        }
        catch
        {
        }
    }
}
