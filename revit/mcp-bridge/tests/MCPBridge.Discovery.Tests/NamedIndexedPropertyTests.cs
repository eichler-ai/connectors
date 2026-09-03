using System.Linq;
using MCPBridge.Core.Discovery;
using MCPBridge.Discovery.Fixtures.VB;
using Xunit;

namespace MCPBridge.Discovery.Tests;

/// <summary>
/// Issue #186: named indexed properties. RevitAPI.dll (C++/CLI) declares 95 per version -- Element.Parameter,
/// Element.Geometry, FamilyInstance.Room, FootPrintRoof.SlopeAngle, ModelCurveArray.Item, ... -- and C# can reach them ONLY
/// through their get_/set_ accessor methods, because `obj[...]` binds to the declaring type's DefaultMember
/// alone. Discovery used to render every indexed property as `T this[...]` (a form that does not compile
/// for these) and, having skipped the accessors as special-name methods, answered member-not-found for the
/// one spelling that does. The fixture is VB because C# cannot declare the shape (see the vbproj).
/// </summary>
public class NamedIndexedPropertyTests
{
    private const string Fixture = "MCPBridge.Discovery.Fixtures.VB.NamedIndexedFixture";

    private static DiscoveryService NewService()
    {
        var cache = new DiscoveryCache(":memory:");
        cache.Sync(new[] { ("core", typeof(NamedIndexedFixture).Assembly) });
        return new DiscoveryService(cache);
    }

    [Fact]
    public void NamedIndexedProperty_RendersAsItsAccessorPair_NotAsAnIndexer()
    {
        var service = NewService();

        var result = service.DescribeFunction($"{Fixture}.SlopeAngle", memberId: null);

        Assert.NotNull(result.Single);
        Assert.Equal("Property", result.Single!.Kind);
        Assert.Equal("double get_SlopeAngle(int curve); void set_SlopeAngle(int curve, double value)", result.Single.Signature);
        Assert.Equal("Retrieve or set the slope angle of the curve.", result.Single.Summary);
        Assert.Single(result.Single.Parameters);
    }

    [Fact]
    public void ReadOnlyNamedIndexedProperty_RendersOnlyTheGetter()
    {
        var service = NewService();

        var result = service.DescribeFunction($"{Fixture}.Overhang", memberId: null);

        Assert.Equal("double get_Overhang(int curve)", result.Single!.Signature);
    }

    [Fact]
    public void DefaultIndexer_StillRendersAsThisBrackets()
    {
        // The DefaultMember case is the one `obj[i]` genuinely binds to, so it keeps the indexer form; the
        // fix must distinguish the two shapes, not swap one blanket rendering for another.
        var service = NewService();

        var result = service.DescribeFunction($"{Fixture}.Item", memberId: null);

        Assert.Equal("int this[int i] { get;set; }", result.Single!.Signature);
    }

    [Theory]
    [InlineData("set_SlopeAngle")]
    [InlineData("get_SlopeAngle")]
    [InlineData("get_Overhang")]
    public void DescribeFunction_AccessorName_ResolvesToTheNamedIndexedProperty(string accessor)
    {
        // The spelling an agent has just typed in a working script, and the one the reflector does not
        // store as a member. Both must land on the property's own record (same member_id as the plain name).
        var service = NewService();
        var byName = service.DescribeFunction($"{Fixture}.{accessor[4..]}", memberId: null);

        var byAccessor = service.DescribeFunction($"{Fixture}.{accessor}", memberId: null);

        Assert.NotNull(byAccessor.Single);
        Assert.Equal(byName.Single!.MemberId, byAccessor.Single!.MemberId);
    }

    [Theory]
    // A plain property: C# reaches it as `obj.Plain`, and `obj.get_Plain()` is a compile error (CS0571),
    // so aliasing it would advertise a spelling that does not work -- exactly the defect being fixed.
    [InlineData("get_Plain")]
    // A read-only named indexed property has no setter, so `set_Overhang(...)` does not compile either.
    [InlineData("set_Overhang")]
    // Nothing named this at all.
    [InlineData("set_Nope")]
    public void DescribeFunction_AccessorName_DoesNotAliasNonIndexedOrMissingMembers(string accessor)
    {
        var service = NewService();

        Assert.Throws<DiscoveryMemberNotFoundException>(() => service.DescribeFunction($"{Fixture}.{accessor}", memberId: null));
    }

    [Fact]
    public void CSharpIndexerWithACustomIndexerName_IsStillTheDefaultMember()
    {
        // Gadget's indexer is [IndexerName("Slot")]: the check must follow DefaultMemberAttribute's value,
        // not assume the literal "Item", or every renamed C# indexer would be misrendered as accessors.
        var cache = new DiscoveryCache(":memory:");
        cache.Sync(new[] { ("core", typeof(Fixtures.Gadget).Assembly) });
        var service = new DiscoveryService(cache);

        var result = service.DescribeFunction("MCPBridge.Discovery.Tests.Fixtures.Gadget.Slot", memberId: null);

        Assert.Equal("int this[int index] { get;set; }", result.Single!.Signature);
    }

    [Fact]
    public void ListFunctions_NamedIndexedProperty_IsListedUnderItsPropertyName()
    {
        // list_functions keeps the one name per member; the accessor spellings live in the signature.
        var service = NewService();

        var result = service.ListFunctions(namespaceFilter: "MCPBridge.Discovery.Fixtures.VB", typeFilter: "NamedIndexedFixture", cursor: null, pageSize: 50);

        Assert.Contains("SlopeAngle", result.Names);
        Assert.DoesNotContain("set_SlopeAngle", result.Names);
    }
}
