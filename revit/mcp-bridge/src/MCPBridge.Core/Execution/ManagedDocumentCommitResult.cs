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
        IReadOnlyList<string> rolledBackDocuments)
    {
        Failure = failure;
        CommitFailures = commitFailures;
        CommittedDocuments = committedDocuments;
        RolledBackDocuments = rolledBackDocuments;
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

    /// <summary>Documents rolled back -- the one that failed, plus every one never attempted.</summary>
    public IReadOnlyList<string> RolledBackDocuments { get; }

    /// <summary>
    /// A failed run that nonetheless left at least one document committed. This is the case that
    /// cannot be papered over: those changes are real and no rollback reaches them.
    /// </summary>
    public bool IsPartial => !Success && CommittedDocuments.Count > 0;

    public static ManagedDocumentCommitResult Succeeded(
        IReadOnlyList<FailureSummary> commitFailures,
        IReadOnlyList<string> committedDocuments) =>
        new(null, commitFailures, committedDocuments, Array.Empty<string>());

    public static ManagedDocumentCommitResult Failed(
        Exception failure,
        IReadOnlyList<FailureSummary> commitFailures,
        IReadOnlyList<string> committedDocuments,
        IReadOnlyList<string> rolledBackDocuments) =>
        new(failure, commitFailures, committedDocuments, rolledBackDocuments);
}
