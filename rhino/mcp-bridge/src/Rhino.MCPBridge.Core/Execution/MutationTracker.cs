namespace Rhino.MCPBridge.Core.Execution;

/// <summary>
/// Nets a run's document events into a <see cref="MutationReport"/>. Pure and tier-1 tested: the
/// adapter feeds it <see cref="DocumentChange"/>s from the main thread during the run; the executor
/// asks for the report after. Netting: an id that ends the run Added counts once as added whatever
/// happened to it after (a replace of a new object is still one new object); an id that existed
/// before the run and was replaced counts as modified; deleted-then-undeleted cancels out; an id
/// added then deleted contributes nothing.
/// </summary>
internal sealed class MutationTracker
{
    private enum State { Added, Modified, Deleted }

    private readonly Dictionary<Guid, (State State, string Type, string Layer)> _net = new();

    public void Record(DocumentChange change)
    {
        var existing = _net.TryGetValue(change.ObjectId, out var e) ? e : ((State?)null, "", "");
        switch (change.Change)
        {
            case DocumentChange.Kind.Added:
                if (existing.Item1 == State.Deleted)
                {
                    // Deleted then re-added with the same id (an undo-like replace): net modified.
                    _net[change.ObjectId] = (State.Modified, change.ObjectType, change.Layer);
                }
                else
                {
                    _net[change.ObjectId] = (State.Added, change.ObjectType, change.Layer);
                }
                break;
            case DocumentChange.Kind.Undeleted:
                if (existing.Item1 == State.Deleted)
                {
                    _net.Remove(change.ObjectId); // cancels the delete
                }
                else
                {
                    _net[change.ObjectId] = (State.Added, change.ObjectType, change.Layer);
                }
                break;
            case DocumentChange.Kind.Deleted:
                if (existing.Item1 == State.Added)
                {
                    _net.Remove(change.ObjectId); // added then deleted: nothing
                }
                else
                {
                    _net[change.ObjectId] = (State.Deleted, change.ObjectType, change.Layer);
                }
                break;
            case DocumentChange.Kind.Replaced:
                if (existing.Item1 == State.Added)
                {
                    _net[change.ObjectId] = (State.Added, change.ObjectType, change.Layer); // still new
                }
                else
                {
                    _net[change.ObjectId] = (State.Modified, change.ObjectType, change.Layer);
                }
                break;
        }
    }

    public MutationReport Build()
    {
        int added = 0, modified = 0, deleted = 0;
        var byType = new Dictionary<string, (int A, int M, int D)>();
        var byLayer = new Dictionary<string, (int A, int M, int D)>();
        foreach (var (_, entry) in _net)
        {
            (int A, int M, int D) t = byType.TryGetValue(entry.Type, out var tv) ? tv : (0, 0, 0);
            var layerKey = entry.Layer.Length == 0 ? "(no layer)" : entry.Layer;
            (int A, int M, int D) l = byLayer.TryGetValue(layerKey, out var lv) ? lv : (0, 0, 0);
            switch (entry.State)
            {
                case State.Added: added++; t.A++; l.A++; break;
                case State.Modified: modified++; t.M++; l.M++; break;
                case State.Deleted: deleted++; t.D++; l.D++; break;
            }
            byType[entry.Type] = t;
            byLayer[layerKey] = l;
        }

        return new MutationReport
        {
            NetAdded = added,
            NetModified = modified,
            NetDeleted = deleted,
            ByObjectType = byType.ToDictionary(kv => kv.Key, kv => new MutationTally { Added = kv.Value.A, Modified = kv.Value.M, Deleted = kv.Value.D }),
            ByLayer = byLayer.ToDictionary(kv => kv.Key, kv => new MutationTally { Added = kv.Value.A, Modified = kv.Value.M, Deleted = kv.Value.D }),
        };
    }
}
