using Rhino.MCPBridge.Core.Execution;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Execution;

/// <summary>PRD §07: NET, not activity. Each case is one netting rule.</summary>
public sealed class MutationTrackerTests
{
    private static DocumentChange C(DocumentChange.Kind k, Guid id, string type = "Brep", string layer = "Default") => new(k, id, type, layer);

    [Fact]
    public void AddedThenDeleted_ContributesNothing()
    {
        var t = new MutationTracker();
        var id = Guid.NewGuid();
        t.Record(C(DocumentChange.Kind.Added, id));
        t.Record(C(DocumentChange.Kind.Deleted, id));
        Assert.True(t.Build().IsEmpty);
    }

    [Fact]
    public void AddedThenReplaced_CountsOnceAsAdded()
    {
        var t = new MutationTracker();
        var id = Guid.NewGuid();
        t.Record(C(DocumentChange.Kind.Added, id));
        t.Record(C(DocumentChange.Kind.Replaced, id));
        t.Record(C(DocumentChange.Kind.Replaced, id));
        var r = t.Build();
        Assert.Equal(1, r.NetAdded);
        Assert.Equal(0, r.NetModified);
    }

    [Fact]
    public void ExistingReplaced_IsModified_Once()
    {
        var t = new MutationTracker();
        var id = Guid.NewGuid();
        t.Record(C(DocumentChange.Kind.Replaced, id));
        t.Record(C(DocumentChange.Kind.Replaced, id));
        Assert.Equal(1, t.Build().NetModified);
    }

    [Fact]
    public void DeletedThenUndeleted_CancelsOut()
    {
        var t = new MutationTracker();
        var id = Guid.NewGuid();
        t.Record(C(DocumentChange.Kind.Deleted, id));
        t.Record(C(DocumentChange.Kind.Undeleted, id));
        Assert.True(t.Build().IsEmpty);
    }

    [Fact]
    public void TalliesByTypeAndLayer_UseTheFinalState()
    {
        var t = new MutationTracker();
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        t.Record(C(DocumentChange.Kind.Added, a, "Brep", "Walls"));
        t.Record(C(DocumentChange.Kind.Added, b, "Curve", "Walls"));
        t.Record(C(DocumentChange.Kind.Deleted, c, "Mesh", ""));
        var r = t.Build();
        Assert.Equal(2, r.NetAdded);
        Assert.Equal(1, r.NetDeleted);
        Assert.Equal(1, r.ByObjectType["Brep"].Added);
        Assert.Equal(1, r.ByObjectType["Curve"].Added);
        Assert.Equal(1, r.ByObjectType["Mesh"].Deleted);
        Assert.Equal(2, r.ByLayer["Walls"].Added);
        Assert.Equal(1, r.ByLayer["(no layer)"].Deleted);
    }
}
