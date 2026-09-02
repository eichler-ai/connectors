using System.Collections.Generic;
using System.Linq;
using MCPBridge.RevitAdapter;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Aggregates one run's <see cref="DocumentChange"/> events into a net <see cref="MutationReport"/>
/// (#146 Phase 2). Pure decision logic over adapter-supplied values, so it is tier-1 testable with
/// hand-built events; the live harness pins what Revit actually raises.
///
/// SET ALGEBRA, NOT COUNTING. Revit raises one event per committed transaction, and a run may commit
/// several (each WithTransaction block, each Settle) and may also see the REVERSE of one (a group rolled
/// back by Settle(keep: false), an undo). Per document this keeps three id sets and applies each event
/// to them:
///   - added    -> created += id; modified -= id; deleted -= id. The last two absorb the undo-then-redo
///                 case (an element modified, undone, redone) and ElementId REUSE after a deletion --
///                 which Revit does across undo -- so a re-issued id counts as the new element it now
///                 is, and the earlier deletion it reverses is no longer reported.
///   - modified -> modified += id, unless the id is already in created (a created element edited later
///                 in the same run is still "created", once)
///   - deleted  -> if created contains id: created -= id (net zero: it never existed for anyone else);
///                 else deleted += id and modified -= id.
/// A rolled-back group's event lists the run's own created ids as DELETED, so the same three rules net
/// it to zero with no special case -- and <see cref="Build"/> additionally drops any document the caller
/// names, for the case where Revit raises no event for the rollback at all.
///
/// BOUNDED (CONVENTIONS.md: every retained buffer states its bound). A script that regenerates a large
/// model can name hundreds of thousands of modified ids per commit, and this object lives in the Revit
/// process for the whole run. Each document retains at most <see cref="RetainedIdCap"/> ids across its
/// three sets; past that, further ids are COUNTED but not retained, so the totals stay exact while the
/// netting and by_category can no longer be, and the document is marked truncated. Categories ride
/// along per id (resolved by the adapter at event time, since Core cannot ask Revit) and are tallied at
/// <see cref="Build"/> from whatever is retained.
///
/// <see cref="DocumentChange.Operation"/> and <see cref="DocumentChange.TransactionNames"/> are not read
/// here: the netting is operation-agnostic by design (an undo IS a reverse delta). They are plumbing for
/// Phase 2b (readable undo labels; the undo/redo tools), translated now so the seam does not change twice.
/// </summary>
internal sealed class MutationTracker
{
    /// <summary>Per-document cap on ids kept for netting and categorisation. 200k longs plus category references is a few MB, an acceptable ceiling for a per-run object.</summary>
    internal const int RetainedIdCap = 200_000;

    private const string NoCategory = "(none)";

    private sealed class DocumentTally
    {
        public readonly Dictionary<long, string?> Created = new();
        public readonly Dictionary<long, string?> Modified = new();
        public readonly HashSet<long> Deleted = new();

        /// <summary>Ids the cap turned away, counted so the totals stay exact.</summary>
        public int UnretainedCreated;
        public int UnretainedModified;
        public int UnretainedDeleted;

        /// <summary>Category resolution capped in the adapter, or id retention capped here -- either way by_category undercounts for this document.</summary>
        public bool Truncated;

        public int Retained => Created.Count + Modified.Count + Deleted.Count;
    }

    private readonly Dictionary<string, DocumentTally> _byDocument = new();

    public void Record(DocumentChange change)
    {
        if (!_byDocument.TryGetValue(change.DocumentId, out var tally))
        {
            tally = new DocumentTally();
            _byDocument[change.DocumentId] = tally;
        }

        tally.Truncated |= change.CategoriesTruncated;

        foreach (var added in change.Added)
        {
            if (tally.Created.ContainsKey(added.Id) || tally.Retained < RetainedIdCap)
            {
                tally.Created[added.Id] = added.Category;
                tally.Modified.Remove(added.Id);
                tally.Deleted.Remove(added.Id);
            }
            else
            {
                tally.UnretainedCreated++;
                tally.Truncated = true;
            }
        }

        foreach (var modified in change.Modified)
        {
            if (tally.Created.ContainsKey(modified.Id))
            {
                continue;
            }

            if (tally.Modified.ContainsKey(modified.Id) || tally.Retained < RetainedIdCap)
            {
                tally.Modified[modified.Id] = modified.Category;
            }
            else
            {
                tally.UnretainedModified++;
                tally.Truncated = true;
            }
        }

        foreach (var deletedId in change.Deleted)
        {
            if (tally.Created.Remove(deletedId))
            {
                continue; // created and deleted within the run: net nothing
            }

            tally.Modified.Remove(deletedId);
            if (tally.Deleted.Contains(deletedId) || tally.Retained < RetainedIdCap)
            {
                tally.Deleted.Add(deletedId);
            }
            else
            {
                tally.UnretainedDeleted++;
                tally.Truncated = true;
            }
        }
    }

    /// <summary>
    /// The net report, or null when nothing remains -- the caller omits the field then. Documents in
    /// <paramref name="excludedDocumentIds"/> (settled with keep: false, whose changes were discarded)
    /// contribute nothing -- not even their truncation flag -- even if Revit raised no rollback event for
    /// them.
    /// </summary>
    public MutationReport? Build(IEnumerable<string>? excludedDocumentIds = null)
    {
        var excluded = excludedDocumentIds is null ? new HashSet<string>() : new HashSet<string>(excludedDocumentIds);
        var created = 0;
        var modified = 0;
        var deleted = 0;
        var truncated = false;
        var byCategory = new Dictionary<string, (int Created, int Modified)>();

        foreach (var (documentId, tally) in _byDocument)
        {
            if (excluded.Contains(documentId))
            {
                continue;
            }

            created += tally.Created.Count + tally.UnretainedCreated;
            modified += tally.Modified.Count + tally.UnretainedModified;
            deleted += tally.Deleted.Count + tally.UnretainedDeleted;
            truncated |= tally.Truncated;

            foreach (var category in tally.Created.Values)
            {
                var key = category ?? NoCategory;
                var current = byCategory.GetValueOrDefault(key);
                byCategory[key] = (current.Created + 1, current.Modified);
            }

            foreach (var category in tally.Modified.Values)
            {
                var key = category ?? NoCategory;
                var current = byCategory.GetValueOrDefault(key);
                byCategory[key] = (current.Created, current.Modified + 1);
            }
        }

        if (created == 0 && modified == 0 && deleted == 0)
        {
            return null;
        }

        var categories = byCategory
            .OrderBy(kv => kv.Key, System.StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => new CategoryTally(kv.Value.Created, kv.Value.Modified), System.StringComparer.Ordinal);

        return new MutationReport(created, modified, deleted, categories, truncated);
    }
}
