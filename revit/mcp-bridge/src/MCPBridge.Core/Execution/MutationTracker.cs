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
///   - added    -> created += id; modified -= id (a create supersedes an earlier "modified" of the same id,
///                 which happens on undo-then-redo)
///   - modified -> modified += id, unless the id is already in created (a created element edited later
///                 in the same run is still "created", once)
///   - deleted  -> if created contains id: created -= id (net zero: it never existed for anyone else);
///                 else deleted += id and modified -= id.
/// A rolled-back group's event lists the run's own created ids as DELETED, so the same three rules net
/// it to zero with no special case -- and <see cref="Build"/> additionally drops any document the caller
/// names, for the case where Revit raises no event for the rollback at all.
///
/// Categories ride along per id (resolved by the adapter at event time, since Core cannot ask Revit)
/// and are tallied at <see cref="Build"/> from whatever is left in the sets.
/// </summary>
internal sealed class MutationTracker
{
    private const string NoCategory = "(none)";

    private sealed class DocumentTally
    {
        public readonly Dictionary<long, string?> Created = new();
        public readonly Dictionary<long, string?> Modified = new();
        public readonly HashSet<long> Deleted = new();
    }

    private readonly Dictionary<string, DocumentTally> _byDocument = new();
    private bool _truncated;

    /// <summary>Every event seen, in order, with its raw operation name -- for the live harness and logs.</summary>
    public IReadOnlyList<(string DocumentId, string Operation, int Added, int Modified, int Deleted)> Events => _events;

    private readonly List<(string, string, int, int, int)> _events = new();

    public void Record(DocumentChange change)
    {
        _events.Add((change.DocumentId, change.OperationName, change.Added.Count, change.Modified.Count, change.Deleted.Count));
        _truncated |= change.CategoriesTruncated;

        if (!_byDocument.TryGetValue(change.DocumentId, out var tally))
        {
            tally = new DocumentTally();
            _byDocument[change.DocumentId] = tally;
        }

        foreach (var added in change.Added)
        {
            tally.Created[added.Id] = added.Category;
            tally.Modified.Remove(added.Id);
            tally.Deleted.Remove(added.Id);
        }

        foreach (var modified in change.Modified)
        {
            if (!tally.Created.ContainsKey(modified.Id))
            {
                tally.Modified[modified.Id] = modified.Category;
            }
        }

        foreach (var deletedId in change.Deleted)
        {
            if (tally.Created.Remove(deletedId))
            {
                continue; // created and deleted within the run: net nothing
            }

            tally.Modified.Remove(deletedId);
            tally.Deleted.Add(deletedId);
        }
    }

    /// <summary>
    /// The net report, or null when nothing remains -- the caller omits the field then. Documents in
    /// <paramref name="excludedDocumentIds"/> (settled with keep: false, whose changes were discarded)
    /// contribute nothing even if Revit raised no rollback event for them.
    /// </summary>
    public MutationReport? Build(IEnumerable<string>? excludedDocumentIds = null)
    {
        var excluded = excludedDocumentIds is null ? new HashSet<string>() : new HashSet<string>(excludedDocumentIds);
        var created = 0;
        var modified = 0;
        var deleted = 0;
        var byCategory = new Dictionary<string, (int Created, int Modified)>();

        foreach (var (documentId, tally) in _byDocument)
        {
            if (excluded.Contains(documentId))
            {
                continue;
            }

            created += tally.Created.Count;
            modified += tally.Modified.Count;
            deleted += tally.Deleted.Count;

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

        return new MutationReport(created, modified, deleted, categories, _truncated);
    }
}
