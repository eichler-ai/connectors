using System;
using System.Collections.Generic;

namespace MCPBridge.RevitAdapter;

/// <summary>
/// One <c>Application.DocumentChanged</c> event, translated to plain values so MCPBridge.Core can
/// aggregate it without naming a Revit type (#146 Phase 2, the mutation report). Element ids are the
/// raw <c>ElementId.Value</c> longs; categories are resolved HERE, in the adapter, at event time --
/// Core cannot call <c>Document.GetElement</c>, and after a deletion there is nothing left to ask, which
/// is why <see cref="Deleted"/> is ids only.
/// </summary>
internal sealed class DocumentChange
{
    public DocumentChange(
        string documentId,
        DocumentChangeOperation operation,
        string operationName,
        IReadOnlyList<string> transactionNames,
        IReadOnlyList<ChangedElement> added,
        IReadOnlyList<ChangedElement> modified,
        IReadOnlyList<long> deleted,
        bool categoriesTruncated)
    {
        DocumentId = documentId;
        Operation = operation;
        OperationName = operationName;
        TransactionNames = transactionNames;
        Added = added;
        Modified = modified;
        Deleted = deleted;
        CategoriesTruncated = categoriesTruncated;
    }

    /// <summary>PRD §09 identity of the changed document, so the tracker can drop a settled-and-discarded document's tally.</summary>
    public string DocumentId { get; }

    public DocumentChangeOperation Operation { get; }

    /// <summary>Revit's own <c>UndoOperation</c> name, verbatim -- kept beside the mapped enum so an unmapped value is still legible in a log.</summary>
    public string OperationName { get; }

    public IReadOnlyList<string> TransactionNames { get; }

    public IReadOnlyList<ChangedElement> Added { get; }

    public IReadOnlyList<ChangedElement> Modified { get; }

    public IReadOnlyList<long> Deleted { get; }

    /// <summary>True when the adapter stopped resolving categories at its cap; the ids are still complete.</summary>
    public bool CategoriesTruncated { get; }
}

/// <summary>An element an event named, with its category resolved at event time (null when it had none or the lookup failed).</summary>
internal readonly record struct ChangedElement(long Id, string? Category);

/// <summary>
/// <c>Autodesk.Revit.DB.Events.UndoOperation</c>, mapped by NAME so this assembly's contract with Core
/// does not depend on which members a given Revit version defines. Anything unrecognised is
/// <see cref="Other"/>, with the raw name preserved on <see cref="DocumentChange.OperationName"/>.
/// </summary>
internal enum DocumentChangeOperation
{
    /// <summary>A transaction committed -- the ordinary write path.</summary>
    Committed,

    /// <summary>An undo: the listed changes are the REVERSE of an earlier commit.</summary>
    Undone,

    /// <summary>A redo.</summary>
    Redone,

    Other,
}

/// <summary>
/// The adapter half of the mutation report (#146 Phase 2): a live subscription to
/// <c>Application.DocumentChanged</c> for the duration of one run. A CAPABILITY interface, like
/// <see cref="IDocumentCreationSource"/>, because only the real <see cref="RevitUiApplicationAdapter"/>
/// has an Application to subscribe to; TransactionScriptExecutor type-tests for it and simply produces
/// no report when the adapter is a fake.
/// </summary>
internal interface IDocumentChangeSource
{
    /// <summary>
    /// Starts delivering every DocumentChanged event to <paramref name="onChange"/> until the returned
    /// handle is disposed. Events arrive on Revit's UI thread, synchronously inside the commit that
    /// caused them -- the same thread the script runs on, so the tracker needs no locking.
    /// </summary>
    IDisposable Subscribe(Action<DocumentChange> onChange);
}
