using System;
using System.Collections.Generic;
using System.Linq;
using MCPBridge.Core.Execution;
using MCPBridge.RevitAdapter;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

/// <summary>
/// #146 Phase 2: the mutation report's decision logic, over hand-built DocumentChange events. What Revit
/// actually raises (one event per commit; a rollback listing the created ids as deleted; category names)
/// is pinned by the live harness; this pins what the tracker DOES with those events.
/// </summary>
public class MutationTrackerTests
{
    private const string Doc = "doc-a";

    private static DocumentChange Committed(string doc = Doc, IEnumerable<(long Id, string? Cat)>? added = null,
        IEnumerable<(long Id, string? Cat)>? modified = null, IEnumerable<long>? deleted = null, bool truncated = false) =>
        new(doc, DocumentChangeOperation.Committed, "TransactionCommitted", new[] { "MCP Bridge Script" },
            (added ?? Array.Empty<(long, string?)>()).Select(a => new ChangedElement(a.Id, a.Cat)).ToArray(),
            (modified ?? Array.Empty<(long, string?)>()).Select(m => new ChangedElement(m.Id, m.Cat)).ToArray(),
            (deleted ?? Array.Empty<long>()).ToArray(),
            truncated);

    [Fact]
    public void Build_ReturnsNull_WhenNothingWasRecorded()
    {
        Assert.Null(new MutationTracker().Build());
    }

    [Fact]
    public void Build_TalliesCreatedAndModifiedByCategory_AndCountsDeleted()
    {
        var tracker = new MutationTracker();
        tracker.Record(Committed(
            added: new (long, string?)[] { (1, "Walls"), (2, "Walls"), (3, "Levels") },
            modified: new (long, string?)[] { (10, "Levels"), (11, null) },
            deleted: new long[] { 20 }));

        var report = tracker.Build()!;

        Assert.Equal(3, report.Created);
        Assert.Equal(2, report.Modified);
        Assert.Equal(1, report.Deleted);
        Assert.Equal(2, report.ByCategory["Walls"].Created);
        Assert.Equal(1, report.ByCategory["Levels"].Created);
        Assert.Equal(1, report.ByCategory["Levels"].Modified);
        Assert.Equal(1, report.ByCategory["(uncategorized)"].Modified);
        Assert.False(report.Truncated);
    }

    [Fact]
    public void CreatedThenDeletedInTheSameRun_NetsToNothing()
    {
        // The element never existed for anyone else, so reporting "1 created, 1 deleted" would send an
        // agent looking for a wall that is not there.
        var tracker = new MutationTracker();
        tracker.Record(Committed(added: new (long, string?)[] { (1, "Walls") }));
        tracker.Record(Committed(deleted: new long[] { 1 }));

        Assert.Null(tracker.Build());
    }

    [Fact]
    public void CreatedThenModified_CountsOnceAsCreated()
    {
        var tracker = new MutationTracker();
        tracker.Record(Committed(added: new (long, string?)[] { (1, "Walls") }));
        tracker.Record(Committed(modified: new (long, string?)[] { (1, "Walls") }));

        var report = tracker.Build()!;

        Assert.Equal(1, report.Created);
        Assert.Equal(0, report.Modified);
    }

    [Fact]
    public void ModifiedThenDeleted_CountsOnlyAsDeleted()
    {
        var tracker = new MutationTracker();
        tracker.Record(Committed(modified: new (long, string?)[] { (5, "Walls") }));
        tracker.Record(Committed(deleted: new long[] { 5 }));

        var report = tracker.Build()!;

        Assert.Equal(0, report.Modified);
        Assert.Equal(1, report.Deleted);
        Assert.False(report.ByCategory.ContainsKey("Walls"));
    }

    [Fact]
    public void ARollbackEventListingTheRunsCreatedIdsAsDeleted_NetsThemOut()
    {
        // The shape a TransactionGroup.RollBack (Settle keep:false) or an undo produces: the reverse of
        // the run's own commit. No special case -- the same three rules net it to zero.
        var tracker = new MutationTracker();
        tracker.Record(Committed(added: new (long, string?)[] { (1, "Walls"), (2, "Walls") }));
        tracker.Record(new DocumentChange(Doc, DocumentChangeOperation.Undone, "TransactionUndone", Array.Empty<string>(),
            Array.Empty<ChangedElement>(), Array.Empty<ChangedElement>(), new long[] { 1, 2 }, categoriesTruncated: false));

        Assert.Null(tracker.Build());
    }

    [Fact]
    public void Build_DropsAnExcludedDocumentEntirely_EvenWithoutARollbackEvent()
    {
        // Belt and braces for Settle(keep: false): the executor knows which documents were discarded,
        // so it need not rely on Revit raising an event for the group rollback.
        var tracker = new MutationTracker();
        tracker.Record(Committed(doc: "doc-kept", added: new (long, string?)[] { (1, "Walls") }));
        tracker.Record(Committed(doc: "doc-discarded", added: new (long, string?)[] { (2, "Walls"), (3, "Floors") }));

        var report = tracker.Build(excludedDocumentIds: new[] { "doc-discarded" })!;

        Assert.Equal(1, report.Created);
        Assert.False(report.ByCategory.ContainsKey("Floors"));
    }

    [Fact]
    public void Build_ReturnsNull_WhenEveryChangedDocumentIsExcluded()
    {
        var tracker = new MutationTracker();
        tracker.Record(Committed(doc: "doc-discarded", added: new (long, string?)[] { (2, "Walls") }));

        Assert.Null(tracker.Build(excludedDocumentIds: new[] { "doc-discarded" }));
    }

    [Fact]
    public void Truncated_IsStickyAcrossEvents()
    {
        var tracker = new MutationTracker();
        tracker.Record(Committed(added: new (long, string?)[] { (1, "Walls") }, truncated: true));
        tracker.Record(Committed(added: new (long, string?)[] { (2, "Walls") }));

        Assert.True(tracker.Build()!.Truncated);
    }

    [Fact]
    public void Truncated_IsPerDocument_SoAnExcludedDocumentsCapDoesNotTaintTheReport()
    {
        var tracker = new MutationTracker();
        tracker.Record(Committed(doc: "doc-kept", added: new (long, string?)[] { (1, "Walls") }));
        tracker.Record(Committed(doc: "doc-discarded", added: new (long, string?)[] { (2, "Walls") }, truncated: true));

        Assert.False(tracker.Build(excludedDocumentIds: new[] { "doc-discarded" })!.Truncated);
    }

    [Fact]
    public void DeletedThenAdded_ElementIdReuse_CountsTheNewElementAndDropsTheDeletion()
    {
        // Revit re-issues ids across undo; the id now names a new element, and the deletion it reverses
        // is no longer part of the run's net effect.
        var tracker = new MutationTracker();
        tracker.Record(Committed(deleted: new long[] { 7 }));
        tracker.Record(Committed(added: new (long, string?)[] { (7, "Doors") }));

        var report = tracker.Build()!;

        Assert.Equal(1, report.Created);
        Assert.Equal(0, report.Deleted);
    }

    [Fact]
    public void ModifiedThenAdded_UndoThenRedo_CountsOnceAsCreated()
    {
        var tracker = new MutationTracker();
        tracker.Record(Committed(modified: new (long, string?)[] { (3, "Walls") }));
        tracker.Record(Committed(added: new (long, string?)[] { (3, "Walls") }));

        var report = tracker.Build()!;

        Assert.Equal(1, report.Created);
        Assert.Equal(0, report.Modified);
    }

    [Fact]
    public void PastTheRetainedIdCap_TotalsStayExact_AndTheReportIsTruncated()
    {
        // CONVENTIONS.md: a stated bound, and an honest report when it bites.
        var tracker = new MutationTracker();
        var many = Enumerable.Range(1, MutationTracker.RetainedIdCap + 5).Select(i => ((long)i, (string?)"Walls"));
        tracker.Record(Committed(added: many));

        var report = tracker.Build()!;

        Assert.Equal(MutationTracker.RetainedIdCap + 5, report.Created);
        Assert.Equal(MutationTracker.RetainedIdCap, report.ByCategory["Walls"].Created);
        Assert.True(report.Truncated);
    }

    [Fact]
    public void SameIdAcrossDifferentDocuments_IsNotConflated()
    {
        // ElementId values are per document; id 1 in two documents is two elements.
        var tracker = new MutationTracker();
        tracker.Record(Committed(doc: "doc-a", added: new (long, string?)[] { (1, "Walls") }));
        tracker.Record(Committed(doc: "doc-b", deleted: new long[] { 1 }));

        var report = tracker.Build()!;

        Assert.Equal(1, report.Created);
        Assert.Equal(1, report.Deleted);
    }

    [Fact]
    public void BuildForDocument_ReportsOnlyThatDocument()
    {
        // #146 Phase 2b: each document's Undo entry is named from ITS effect, not the run's.
        var tracker = new MutationTracker();
        tracker.Record(Committed(doc: "doc-model", modified: new (long, string?)[] { (1, "Walls"), (2, "Walls") }));
        tracker.Record(Committed(doc: "doc-scratch", added: new (long, string?)[] { (1, "Levels"), (2, "Levels"), (3, "Levels") }));

        var scratch = tracker.BuildForDocument("doc-scratch")!;
        Assert.Equal(3, scratch.Created);
        Assert.Equal(0, scratch.Modified);

        var model = tracker.BuildForDocument("doc-model")!;
        Assert.Equal(0, model.Created);
        Assert.Equal(2, model.Modified);

        Assert.Null(tracker.BuildForDocument("doc-untouched"));
    }

    [Fact]
    public void ByCategory_IsOrderedByName_ForAStableWireShape()
    {
        var tracker = new MutationTracker();
        tracker.Record(Committed(added: new (long, string?)[] { (1, "Walls"), (2, "Doors"), (3, "Levels") }));

        Assert.Equal(new[] { "Doors", "Levels", "Walls" }, tracker.Build()!.ByCategory.Keys.ToArray());
    }
}
