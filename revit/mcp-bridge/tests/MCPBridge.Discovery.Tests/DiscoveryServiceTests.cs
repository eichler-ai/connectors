using System.Linq;
using MCPBridge.Core.Discovery;
using MCPBridge.Core.Protocol;
using MCPBridge.Discovery.Tests.Fixtures;
using Xunit;

namespace MCPBridge.Discovery.Tests;

/// <summary>
/// End-to-end coverage of <see cref="DiscoveryService"/> (list_functions/search_functions/describe_function,
/// PRD §08), reflecting over this test assembly's own Fixtures/*.cs types -- a portable, self-contained
/// target (no real, proprietary RevitAPI.dll/xml needed) -- joined against the real XML-doc sidecar the
/// compiler emits from those fixtures' triple-slash comments (MCPBridge.Discovery.Tests.xml, next to this
/// assembly's own DLL; see the csproj's GenerateDocumentationFile=true and its own comment).
/// </summary>
public class DiscoveryServiceTests
{
    private static DiscoveryService NewService() => new(new DiscoveryOptions
    {
        Assemblies = new[] { typeof(Widget).Assembly },
    });

    // ---------------------------------------------------------------------------------------------
    // list_functions
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ListFunctions_ScopedByType_ReturnsOnlyThatTypesPublicMembers()
    {
        var service = NewService();

        var result = service.ListFunctions(namespaceFilter: null, typeFilter: "MCPBridge.Discovery.Tests.Fixtures.Widget", cursor: null, pageSize: 100);

        Assert.All(result.Members, m => Assert.Equal("MCPBridge.Discovery.Tests.Fixtures.Widget", m.DeclaringType));
        Assert.Contains(result.Members, m => m.Name == "Describe" && m.Kind == "Method");
        Assert.Contains(result.Members, m => m.Name == "Id" && m.Kind == "Property");
        Assert.Contains(result.Members, m => m.Kind == "Constructor");
        Assert.Contains(result.Members, m => m.Name == "Name" && m.Kind == "Field");
        Assert.Contains(result.Members, m => m.Name == "Changed" && m.Kind == "Event");
        Assert.DoesNotContain(result.Members, m => m.Name == "Hidden"); // internal -- must never appear
        Assert.Equal(result.Members.Count, result.TotalScoped);
    }

    [Fact]
    public void ListFunctions_ScopedByNamespace_FlattensMembersAcrossTypesInThatNamespaceOnly()
    {
        var service = NewService();

        var result = service.ListFunctions(namespaceFilter: "MCPBridge.Discovery.Tests.Fixtures", typeFilter: null, cursor: null, pageSize: 500);

        Assert.Contains(result.Members, m => m.DeclaringType == "MCPBridge.Discovery.Tests.Fixtures.Widget");
        Assert.Contains(result.Members, m => m.DeclaringType == "MCPBridge.Discovery.Tests.Fixtures.Gadget");
        Assert.DoesNotContain(result.Members, m => m.DeclaringType == "MCPBridge.Discovery.Tests.Fixtures.Other.Thing");
    }

    [Fact]
    public void ListFunctions_Unscoped_ReturnsTypesNotMembers()
    {
        var service = NewService();

        var result = service.ListFunctions(namespaceFilter: null, typeFilter: null, cursor: null, pageSize: 100_000);

        Assert.Contains(result.Members, m => m.Kind == "Type" && m.MemberId == "T:MCPBridge.Discovery.Tests.Fixtures.Widget");
        Assert.Contains(result.Members, m => m.Kind == "Type" && m.MemberId == "T:MCPBridge.Discovery.Tests.Fixtures.Other.Thing");
        Assert.True(result.TotalScoped > 0);
    }

    [Fact]
    public void ListFunctions_Pagination_NextCursorPresentThenAbsent()
    {
        var service = NewService();

        var page1 = service.ListFunctions(namespaceFilter: null, typeFilter: "MCPBridge.Discovery.Tests.Fixtures.Widget", cursor: null, pageSize: 1);
        Assert.Single(page1.Members);
        Assert.NotNull(page1.NextCursor);
        var totalScoped = page1.TotalScoped;

        // Walk the whole scoped list one page at a time and confirm we land on exactly totalScoped members
        // with no duplicates, and the final page has no next_cursor.
        var seen = new System.Collections.Generic.HashSet<string>();
        string? cursor = null;
        int guard = 0;
        while (true)
        {
            var page = service.ListFunctions(null, "MCPBridge.Discovery.Tests.Fixtures.Widget", cursor, pageSize: 1);
            foreach (var m in page.Members)
            {
                Assert.True(seen.Add(m.MemberId), $"duplicate member across pages: {m.MemberId}");
            }

            if (page.NextCursor is null)
            {
                break;
            }

            cursor = page.NextCursor;
            Assert.True(++guard < 1000, "pagination did not terminate");
        }

        Assert.Equal(totalScoped, seen.Count);
    }

    [Fact]
    public void ListFunctions_InvalidCursor_ThrowsJsonRpcParamException()
    {
        var service = NewService();

        Assert.Throws<JsonRpcParamException>(() =>
            service.ListFunctions(null, "MCPBridge.Discovery.Tests.Fixtures.Widget", cursor: "not-a-number", pageSize: 50));
    }

    [Fact]
    public void ListFunctions_JoinsXmlDocSummary()
    {
        var service = NewService();

        var result = service.ListFunctions(null, "MCPBridge.Discovery.Tests.Fixtures.Gadget", null, 100);

        var run = Assert.Single(result.Members, m => m.Name == "Run");
        Assert.Equal("Runs the gadget.", run.Summary);
    }

    [Fact]
    public void ListFunctions_TruncatesLongSummaries()
    {
        var service = NewService();

        var result = service.ListFunctions(null, "MCPBridge.Discovery.Tests.Fixtures.Widget", null, 100);

        var longMember = Assert.Single(result.Members, m => m.Name == "LongSummaryMethod");
        Assert.NotNull(longMember.Summary);
        Assert.True(longMember.Summary!.Length <= 303); // 300 + "..."
    }

    // ---------------------------------------------------------------------------------------------
    // search_functions
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SearchFunctions_ExactNameMatch_RanksAboveUnrelatedMatches()
    {
        var service = NewService();

        var result = service.SearchFunctions("Describe", cursor: null, topN: 50);

        Assert.NotEmpty(result.Results);
        var top = result.Results[0];
        Assert.Equal("Describe", top.Member.Name);
        Assert.True(result.Results.Select(r => r.Score).SequenceEqual(result.Results.Select(r => r.Score).OrderByDescending(s => s)));
    }

    [Fact]
    public void SearchFunctions_NoMatches_ReturnsEmptyWithZeroTotal()
    {
        var service = NewService();

        var result = service.SearchFunctions("zzzznonexistentqueryzzzz", cursor: null, topN: 20);

        Assert.Empty(result.Results);
        Assert.Equal(0, result.TotalMatched);
        Assert.Null(result.NextCursor);
    }

    [Fact]
    public void SearchFunctions_Pagination_TotalMatchedIsStableAcrossPages()
    {
        var service = NewService();

        var page1 = service.SearchFunctions("Widget", cursor: null, topN: 1);
        Assert.True(page1.TotalMatched >= 1);

        if (page1.NextCursor is not null)
        {
            var page2 = service.SearchFunctions("Widget", cursor: page1.NextCursor, topN: 1);
            Assert.Equal(page1.TotalMatched, page2.TotalMatched);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // describe_function
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void DescribeFunction_SingleOverload_ReturnsFullDocShape()
    {
        var service = NewService();

        var result = service.DescribeFunction("MCPBridge.Discovery.Tests.Fixtures.Gadget.Run", overloadIndex: null, memberId: null);

        Assert.NotNull(result.Single);
        Assert.Null(result.Overloads);
        Assert.Equal("Runs the gadget.", result.Single!.Summary);
        Assert.Equal(1, result.Single.OverloadCount);
        Assert.Empty(result.Single.Parameters);
    }

    [Fact]
    public void DescribeFunction_MultipleOverloads_NoDisambiguation_ReturnsCompactList()
    {
        var service = NewService();

        var result = service.DescribeFunction("MCPBridge.Discovery.Tests.Fixtures.Widget.Describe", overloadIndex: null, memberId: null);

        Assert.Null(result.Single);
        Assert.NotNull(result.Overloads);
        Assert.Equal("MCPBridge.Discovery.Tests.Fixtures.Widget.Describe", result.Overloads!.Member);
        Assert.Equal(2, result.Overloads.Overloads.Count);
        Assert.All(result.Overloads.Overloads, o => Assert.Contains("Describe", o.Signature));
    }

    [Fact]
    public void DescribeFunction_DisambiguatedByOverloadIndex_ReturnsFullDocShape()
    {
        var service = NewService();

        var result = service.DescribeFunction("MCPBridge.Discovery.Tests.Fixtures.Widget.Describe", overloadIndex: 0, memberId: null);

        Assert.NotNull(result.Single);
        Assert.Equal(2, result.Single!.OverloadCount);
    }

    [Fact]
    public void DescribeFunction_DisambiguatedByMemberId_MatchesOverloadIndexResolution()
    {
        var service = NewService();

        var byIndex = service.DescribeFunction("MCPBridge.Discovery.Tests.Fixtures.Widget.Describe", overloadIndex: 1, memberId: null);
        var byId = service.DescribeFunction("MCPBridge.Discovery.Tests.Fixtures.Widget.Describe", overloadIndex: null, memberId: byIndex.Single!.MemberId);

        Assert.Equal(byIndex.Single.MemberId, byId.Single!.MemberId);
        Assert.Equal(byIndex.Single.Signature, byId.Single.Signature);
    }

    [Fact]
    public void DescribeFunction_ParameterDescriptionsJoinedFromXmlDoc()
    {
        var service = NewService();

        var result = service.DescribeFunction("MCPBridge.Discovery.Tests.Fixtures.Widget.Describe", overloadIndex: 1, memberId: null);

        var param = Assert.Single(result.Single!.Parameters);
        Assert.Equal("detailLevel", param.Name);
        Assert.Equal("How much detail to include.", param.Description);
    }

    [Fact]
    public void DescribeFunction_UnknownMember_ThrowsNotFound()
    {
        var service = NewService();

        Assert.Throws<DiscoveryMemberNotFoundException>(() =>
            service.DescribeFunction("MCPBridge.Discovery.Tests.Fixtures.Widget.NoSuchMethod", null, null));
    }

    [Fact]
    public void DescribeFunction_UnknownType_ThrowsNotFound()
    {
        var service = NewService();

        Assert.Throws<DiscoveryMemberNotFoundException>(() =>
            service.DescribeFunction("MCPBridge.Discovery.Tests.Fixtures.NoSuchType.Foo", null, null));
    }

    [Fact]
    public void DescribeFunction_OutOfRangeOverloadIndex_ThrowsNotFound()
    {
        var service = NewService();

        Assert.Throws<DiscoveryMemberNotFoundException>(() =>
            service.DescribeFunction("MCPBridge.Discovery.Tests.Fixtures.Widget.Describe", overloadIndex: 99, memberId: null));
    }

    [Fact]
    public void DescribeFunction_Constructor_ResolvesViaCtorKeyword()
    {
        var service = NewService();

        var result = service.DescribeFunction("MCPBridge.Discovery.Tests.Fixtures.Widget.ctor", overloadIndex: null, memberId: null);

        // Widget has two constructors -> ambiguous without disambiguation.
        Assert.NotNull(result.Overloads);
        Assert.Equal(2, result.Overloads!.Overloads.Count);
    }
}
