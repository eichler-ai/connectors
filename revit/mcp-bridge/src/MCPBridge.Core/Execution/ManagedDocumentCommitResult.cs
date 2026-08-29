using System;
using System.Collections.Generic;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Execution;

/// <summary>
/// The outcome of committing every document a script touched (issue #24) -- the ambient document plus
/// each one the script created via ScriptGlobals.CreateProjectDocument/CreateFamilyDocument.
///
/// It carries WHICH documents committed and which rolled back, not just a boolean, because with N
/// documents a failure can be genuinely partial: Revit has no way to un-commit an already-committed
/// Transaction, so if document 2's commit fails after document 1's succeeded, document 1's changes
/// stay. PRD §01's observability-over-silence principle makes reporting that non-optional -- see
/// TransactionScriptExecutor's `script-partial-commit` notice, which is built from these lists.
/// </summary>
public sealed class ManagedDocumentCommitResult
{
    private ManagedDocumentCommitResult(
        Exception? failure,
        IReadOnlyList<FailureSummary> commitFailures,
        IReadOnlyList<string> committedDocuments,
        IReadOnlyList<string> rolledBackDocuments,
        IReadOnlyList<string> unknownStateDocuments,
        bool anyCommittedDocumentMayBeReal)
    {
        Failure = failure;
        CommitFailures = commitFailures;
        CommittedDocuments = committedDocuments;
        RolledBackDocuments = rolledBackDocuments;
        UnknownStateDocuments = unknownStateDocuments;
        AnyCommittedDocumentMayBeReal = anyCommittedDocumentMayBeReal;
    }

    /// <summary>True when every document committed and every group assimilated.</summary>
    public bool Success => Failure is null;

    /// <summary>The exception to report when <see cref="Success"/> is false; null otherwise.</summary>
    public Exception? Failure { get; }

    /// <summary>
    /// Every Failures-API summary seen across every document's commit, in commit order (PRD §07).
    /// Aggregated rather than per-document because notices[] is one flat list.
    /// </summary>
    public IReadOnlyList<FailureSummary> CommitFailures { get; }

    /// <summary>Documents whose transaction committed AND whose group assimilated, in commit order.</summary>
    public IReadOnlyList<string> CommittedDocuments { get; }

    /// <summary>
    /// Documents whose rollback was ATTEMPTED AND OBSERVED TO SUCCEED -- the one that failed, plus every
    /// one never attempted. A document only appears here when every rollback call for it returned without
    /// throwing; if any threw, it is in <see cref="UnknownStateDocuments"/> instead.
    /// </summary>
    public IReadOnlyList<string> RolledBackDocuments { get; }

    /// <summary>
    /// Documents whose own rollback THREW, so this connector cannot say whether their changes were
    /// undone (independent PR review finding). These used to be reported as cleanly rolled back, which
    /// was a straightforward lie about the one thing this whole result type exists to be honest about --
    /// and the ambient document, a real model a human has open, is exactly the document it could be lied
    /// about for.
    ///
    /// Reported as its own list rather than merged into <see cref="RolledBackDocuments"/> (which would
    /// overclaim) or dropped (which would violate PRD §01's observability-over-silence: a document in an
    /// unknown state is the MOST important one to name, not the one to quietly omit). Rollback stays
    /// best-effort -- a rollback exception must never mask the original commit failure, nor stop the next
    /// document's rollback -- so the fix is to report the outcome, not to start throwing.
    /// </summary>
    public IReadOnlyList<string> UnknownStateDocuments { get; }

    /// <summary>
    /// A failed run that nonetheless left at least one document committed, or left one in an
    /// indeterminate state. Both are cases that cannot be papered over: committed changes are real and no
    /// rollback reaches them, and an unknown-state document is precisely what an agent must be told to go
    /// look at.
    /// </summary>
    public bool IsPartial => !Success && (CommittedDocuments.Count > 0 || UnknownStateDocuments.Count > 0);

    /// <summary>
    /// True when a committed document could be a document the script did NOT itself create this run --
    /// i.e. one adopted via ScriptGlobals.OpenForWriting (DocumentOrigin.AdoptedExisting), or the ambient
    /// document itself (DocumentOrigin.Ambient). Independent PR review finding: PartialCommitNotice's
    /// remedy text used to unconditionally claim every committed document is "unsaved and in-memory," which
    /// was true when CreateProjectDocument/CreateFamilyDocument were the only two members of this tier, but
    /// is a straightforward lie about an adopted or ambient document -- both can be real, saved, on-disk
    /// models. This flag is what lets that remedy text tell the two cases apart.
    /// </summary>
    public bool AnyCommittedDocumentMayBeReal { get; }

    public static ManagedDocumentCommitResult Succeeded(
        IReadOnlyList<FailureSummary> commitFailures,
        IReadOnlyList<string> committedDocuments,
        bool anyCommittedDocumentMayBeReal = false) =>
        new(null, commitFailures, committedDocuments, Array.Empty<string>(), Array.Empty<string>(), anyCommittedDocumentMayBeReal);

    public static ManagedDocumentCommitResult Failed(
        Exception failure,
        IReadOnlyList<FailureSummary> commitFailures,
        IReadOnlyList<string> committedDocuments,
        IReadOnlyList<string> rolledBackDocuments,
        IReadOnlyList<string> unknownStateDocuments,
        bool anyCommittedDocumentMayBeReal = false) =>
        new(failure, commitFailures, committedDocuments, rolledBackDocuments, unknownStateDocuments, anyCommittedDocumentMayBeReal);
}
