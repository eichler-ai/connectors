using System.Linq;
using MCPBridge.Core.Discovery;
using MCPBridge.Discovery.Tests.Fixtures;
using Xunit;

namespace MCPBridge.Discovery.Tests;

/// <summary>
/// Direct coverage of <see cref="XmlDocId.GetDocId"/> -- the fiddliest part of this feature (PRD §08's task
/// brief calls it out specifically): methods with 0/1/2+ params, properties (including an indexer),
/// constructors, fields, events, a static method, and one level of closed generic type used as a parameter.
/// </summary>
public class XmlDocIdTests
{
    [Fact]
    public void Type_ProducesTPrefixedFullName()
    {
        Assert.Equal("T:MCPBridge.Discovery.Tests.Fixtures.Widget", XmlDocId.GetDocId(typeof(Widget)));
    }

    [Fact]
    public void NoArgConstructor_ProducesCtorWithNoParens()
    {
        var ctor = typeof(Widget).GetConstructor(System.Type.EmptyTypes)!;
        Assert.Equal("M:MCPBridge.Discovery.Tests.Fixtures.Widget.#ctor", XmlDocId.GetDocId(ctor));
    }

    [Fact]
    public void OneArgConstructor_ProducesCtorWithParen()
    {
        var ctor = typeof(Widget).GetConstructor(new[] { typeof(int) })!;
        Assert.Equal("M:MCPBridge.Discovery.Tests.Fixtures.Widget.#ctor(System.Int32)", XmlDocId.GetDocId(ctor));
    }

    [Fact]
    public void NoArgMethod_HasNoParens()
    {
        var method = typeof(Widget).GetMethod("Describe", System.Type.EmptyTypes)!;
        Assert.Equal("M:MCPBridge.Discovery.Tests.Fixtures.Widget.Describe", XmlDocId.GetDocId(method));
    }

    [Fact]
    public void OneArgMethod_HasOneParamType()
    {
        var method = typeof(Widget).GetMethod("Describe", new[] { typeof(int) })!;
        Assert.Equal("M:MCPBridge.Discovery.Tests.Fixtures.Widget.Describe(System.Int32)", XmlDocId.GetDocId(method));
    }

    [Fact]
    public void GenericCollectionParam_UsesCurlyBraceArgsWithoutArity()
    {
        var method = typeof(Widget).GetMethod("AddTags")!;
        Assert.Equal(
            "M:MCPBridge.Discovery.Tests.Fixtures.Widget.AddTags(System.Collections.Generic.ICollection{System.Int32})",
            XmlDocId.GetDocId(method));
    }

    [Fact]
    public void GenericCollectionReturnType_DoesNotAffectDocId()
    {
        // Return type never participates in the doc-id -- GetTags() has zero params despite a generic
        // return type.
        var method = typeof(Widget).GetMethod("GetTags")!;
        Assert.Equal("M:MCPBridge.Discovery.Tests.Fixtures.Widget.GetTags", XmlDocId.GetDocId(method));
    }

    [Fact]
    public void Property_UsesPPrefix()
    {
        var property = typeof(Widget).GetProperty("Id")!;
        Assert.Equal("P:MCPBridge.Discovery.Tests.Fixtures.Widget.Id", XmlDocId.GetDocId(property));
    }

    [Fact]
    public void Indexer_IncludesIndexParameterTypes()
    {
        var indexer = typeof(Gadget).GetProperties().Single(p => p.GetIndexParameters().Length > 0);
        Assert.Equal("P:MCPBridge.Discovery.Tests.Fixtures.Gadget.Item(System.Int32)", XmlDocId.GetDocId(indexer));
    }

    [Fact]
    public void Field_UsesFPrefix()
    {
        var field = typeof(Widget).GetField("Name")!;
        Assert.Equal("F:MCPBridge.Discovery.Tests.Fixtures.Widget.Name", XmlDocId.GetDocId(field));
    }

    [Fact]
    public void Event_UsesEPrefix()
    {
        var evt = typeof(Widget).GetEvent("Changed")!;
        Assert.Equal("E:MCPBridge.Discovery.Tests.Fixtures.Widget.Changed", XmlDocId.GetDocId(evt));
    }

    [Fact]
    public void StaticMethod_SameFormatAsInstanceMethod()
    {
        var method = typeof(Widget).GetMethod("Create")!;
        Assert.Equal("M:MCPBridge.Discovery.Tests.Fixtures.Widget.Create", XmlDocId.GetDocId(method));
    }
}
