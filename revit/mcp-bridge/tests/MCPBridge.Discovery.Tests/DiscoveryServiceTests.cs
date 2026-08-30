using System.Linq;
using MCPBridge.Core.Discovery;
using MCPBridge.Core.Protocol;
using MCPBridge.Discovery.Tests.Fixtures;
using Xunit;

namespace MCPBridge.Discovery.Tests;

/// <summary>
/// End-to-end coverage of <see cref="DiscoveryService"/> (list_functions/search_functions/describe_function,
/// PRD §08), backed by a <see cref="DiscoveryCache"/> synced against this test assembly's own Fixtures/*.cs
/// types -- a portable, self-contained target (no real, proprietary RevitAPI.dll/xml needed) -- joined
/// against the real XML-doc sidecar the compiler emits from those fixtures' triple-slash comments
/// (MCPBridge.Discovery.Tests.xml, next to this assembly's own DLL; see the csproj's
/// GenerateDocumentationFile=true and its own comment). Uses a fresh in-memory (":memory:") cache per test
/// via <see cref="NewService"/> -- no real file, no cross-test interference.
/// </summary>
public class DiscoveryServiceTests
{
    private const string FixturesNamespace = "MCPBridge.Discovery.Tests.Fixtures";
    private const string OtherNamespace = "MCPBridge.Discovery.Tests.Fixtures.Other";

    private static DiscoveryService NewService()
    {
        var cache = new DiscoveryCache(":memory:");
        cache.Sync(new[] { ("core", typeof(Widget).Assembly) });
        return new DiscoveryService(cache);
    }

    // ---------------------------------------------------------------------------------------------
    // list_functions -- strict one-level-at-a-time tree (PRD §08 addendum)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ListFunctions_NoArgs_ReturnsNamespacesOnly()
    {
        var service = NewService();

        var result = service.ListFunctions(namespaceFilter: null, typeFilter: null, cursor: null, pageSize: 100);

        Assert.Equal(ListFunctionsTier.Namespaces, result.Tier);
        Assert.Contains(FixturesNamespace, result.Names);
        Assert.Contains(OtherNamespace, result.Names);
        Assert.NotNull(result.Counts);
        Assert.Equal(result.Names.Count, result.Counts!.Count);
        Assert.True(result.TotalScoped > 0);
    }

    [Fact]
    public void ListFunctions_NamespaceOnly_ReturnsTypeNamesInThatNamespaceOnly()
    {
        var service = NewService();

        var result = service.ListFunctions(namespaceFilter: FixturesNamespace, typeFilter: null, cursor: null, pageSize: 500);

        Assert.Equal(ListFunctionsTier.Types, result.Tier);
        Assert.Contains("Widget", result.Names);
        Assert.Contains("Gadget", result.Names);
        Assert.DoesNotContain("Thing", result.Names); // lives in the Other sub-namespace, must not leak in.
    }

    [Fact]
    public void ListFunctions_NamespaceAndType_ReturnsDistinctMemberNamesOnly()
    {
        var service = NewService();

        var result = service.ListFunctions(namespaceFilter: FixturesNamespace, typeFilter: "Widget", cursor: null, pageSize: 100);

        Assert.Equal(ListFunctionsTier.Members, result.Tier);
        Assert.Contains("Describe", result.Names); // Widget.Describe has 2 overloads -- must appear once, not twice.
        Assert.Equal(1, result.Names.Count(n => n == "Describe"));
        Assert.Contains("Id", result.Names);
        Assert.Contains("Name", result.Names);
        Assert.Contains("Changed", result.Names);
        Assert.DoesNotContain("Hidden", result.Names); // internal -- must never appear
    }

    [Fact]
    public void ListFunctions_FullyQualifiedTypeName_StripsNamespacePrefixAndStillResolves()
    {
        // params.type_name is documented as bare/prefix-stripped (matching the types tier's own output),
        // but a caller passing the fully-qualified form back (e.g. copied verbatim from a jsonschema
        // example, or from describe_function's own "member" convention) must not get a silent empty
        // result over it.
        var service = NewService();

        var result = service.ListFunctions(namespaceFilter: FixturesNamespace, typeFilter: FixturesNamespace + ".Widget", cursor: null, pageSize: 100);

        Assert.Equal(ListFunctionsTier.Members, result.Tier);
        Assert.Equal("Widget", result.TypeName); // echoed back bare, not fully-qualified.
        Assert.Contains("Describe", result.Names);
        Assert.Contains("Id", result.Names);
    }

    [Fact]
    public void ListFunctions_TypeWithoutNamespace_ThrowsJsonRpcParamException()
    {
        var service = NewService();

        Assert.Throws<JsonRpcParamException>(() =>
            service.ListFunctions(namespaceFilter: null, typeFilter: "Widget", cursor: null, pageSize: 100));
    }

    [Fact]
    public void ListFunctions_Pagination_NextCursorPresentThenAbsent()
    {
        var service = NewService();

        var page1 = service.ListFunctions(namespaceFilter: FixturesNamespace, typeFilter: "Widget", cursor: null, pageSize: 1);
        Assert.Single(page1.Names);
        Assert.NotNull(page1.NextCursor);
        var totalScoped = page1.TotalScoped;

        var seen = new System.Collections.Generic.HashSet<string>();
        string? cursor = null;
        var guard = 0;
        while (true)
        {
            var page = service.ListFunctions(FixturesNamespace, "Widget", cursor, pageSize: 1);
            foreach (var n in page.Names)
            {
                Assert.True(seen.Add(n), $"duplicate name across pages: {n}");
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
            service.ListFunctions(FixturesNamespace, "Widget", cursor: "not-a-number", pageSize: 50));
    }

    // ---------------------------------------------------------------------------------------------
    // Type-surface filtering (which types count as "the API" at all)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void PublicTypeNestedInInternalType_IsNeverDiscoverable()
    {
        // IsNestedPublic is true for InternalOuter.NestedPublic, but Type.IsVisible is false -- nothing
        // outside the assembly can reach it, so it must not appear on any discovery path.
        const string nestedNamespace = FixturesNamespace;
        const string nestedFullName = FixturesNamespace + ".InternalOuter.NestedPublic";
        var service = NewService();

        var types = service.ListFunctions(nestedNamespace, null, null, pageSize: 100_000);
        Assert.DoesNotContain("InternalOuter", types.Names);
        Assert.DoesNotContain("NestedPublic", types.Names);

        var byType = service.ListFunctions(nestedNamespace, "NestedPublic", null, pageSize: 100);
        Assert.Empty(byType.Names);
        Assert.Equal(0, byType.TotalScoped);

        Assert.Throws<DiscoveryMemberNotFoundException>(() =>
            service.DescribeFunction(nestedFullName + ".NestedPublicWork", null));
    }

    [Fact]
    public void UndocumentedType_IsHiddenFromBrowsing_ButStillReachableByExplicitLookup()
    {
        // The RevitAPI.dll C++/CLI-metadata-noise filter: types with no XML-doc entry are dropped from the
        // browse surface (the namespace-scoped type list), but must never become unreachable -- an explicit
        // type_name scope and describe_function both still resolve them.
        const string undocumented = "Undocumented";
        const string undocumentedFullName = FixturesNamespace + "." + undocumented;
        var service = NewService();

        var types = service.ListFunctions(FixturesNamespace, null, null, pageSize: 100_000);
        Assert.DoesNotContain(undocumented, types.Names);

        var byType = service.ListFunctions(FixturesNamespace, undocumented, null, pageSize: 100);
        Assert.Contains("UndocumentedWork", byType.Names);

        var described = service.DescribeFunction(undocumentedFullName + ".UndocumentedWork", null);
        Assert.NotNull(described.Single);
        Assert.Equal("UndocumentedWork", described.Single!.Name);
    }

    // The no-sidecar escape hatch (an assembly with no XML-doc file gets no documented-types narrowing at
    // all, rather than an empty discovery surface) is covered by MCPBridge.Core.Tests' DiscoveryDispatchTests:
    // that project deliberately does not set GenerateDocumentationFile, so every one of its discovery tests
    // only passes if the escape hatch holds.

    // ---------------------------------------------------------------------------------------------
    // search_functions
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SearchFunctions_ExactNameMatch_RanksAboveUnrelatedMatches()
    {
        var service = NewService();

        var result = service.SearchFunctions("Describe", namespaceFilter: null, cursor: null, topN: 50);

        Assert.NotEmpty(result.Results);
        var top = result.Results[0];
        Assert.Equal("Describe", top.Member.Name);
        Assert.True(result.Results.Select(r => r.Score).SequenceEqual(result.Results.Select(r => r.Score).OrderByDescending(s => s)));
    }

    [Fact]
    public void SearchFunctions_ExactTypeDotMember_RanksHighestTier()
    {
        var service = NewService();

        var result = service.SearchFunctions("Gadget.Run", namespaceFilter: null, cursor: null, topN: 50);

        Assert.NotEmpty(result.Results);
        Assert.Equal("Run", result.Results[0].Member.Name);
        Assert.Equal("Gadget", result.Results[0].Member.DeclaringType.Split('.').Last());
        Assert.True(result.Results[0].Score >= 1000);
    }

    [Fact]
    public void SearchFunctions_TypeAndMemberTokens_RanksAboveSummaryOnlyMatch()
    {
        var service = NewService();

        // "widg desc" -- both tokens are partial (not exact) substring matches against {type name, member
        // name} for Widget.Describe, so this is deliberately NOT an exact Type.Member pair (tier 1 would
        // require the tokens to equal the real names) -- it must land in tier 2, still above anything that
        // only matches via a summary/FTS5 fallback hit (tier 3, capped below 500).
        var result = service.SearchFunctions("widg desc", namespaceFilter: null, cursor: null, topN: 50);

        Assert.NotEmpty(result.Results);
        Assert.Equal("Describe", result.Results[0].Member.Name);
        Assert.InRange(result.Results[0].Score, 500, 999);
    }

    [Fact]
    public void SearchFunctions_NamespaceFilter_ExcludesOtherNamespaces()
    {
        var service = NewService();

        var result = service.SearchFunctions("Do", namespaceFilter: FixturesNamespace, cursor: null, topN: 50);

        Assert.DoesNotContain(result.Results, r => r.Member.Namespace == OtherNamespace);
    }

    [Fact]
    public void SearchFunctions_NoMatches_ReturnsEmptyWithZeroTotal()
    {
        var service = NewService();

        var result = service.SearchFunctions("zzzznonexistentqueryzzzz", namespaceFilter: null, cursor: null, topN: 20);

        Assert.Empty(result.Results);
        Assert.Equal(0, result.TotalMatched);
        Assert.Null(result.NextCursor);
    }

    [Fact]
    public void SearchFunctions_Pagination_TotalMatchedIsStableAcrossPages()
    {
        var service = NewService();

        var page1 = service.SearchFunctions("Widget", namespaceFilter: null, cursor: null, topN: 1);
        Assert.True(page1.TotalMatched >= 1);

        if (page1.NextCursor is not null)
        {
            var page2 = service.SearchFunctions("Widget", namespaceFilter: null, cursor: page1.NextCursor, topN: 1);
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

        var result = service.DescribeFunction("MCPBridge.Discovery.Tests.Fixtures.Gadget.Run", memberId: null);

        Assert.NotNull(result.Single);
        Assert.Null(result.Overloads);
        Assert.Equal("Runs the gadget.", result.Single!.Summary);
        Assert.Equal(1, result.Single.OverloadCount);
        Assert.Empty(result.Single.Parameters);
    }

    [Fact]
    public void DescribeFunction_MultipleOverloads_NoDisambiguation_ReturnsCompactList()
    {
        // Ambiguous member, neither disambiguator supplied -- the overloads[] list is now the SOLE
        // disambiguation mechanism (overload_index removed, issue #64).
        var service = NewService();

        var result = service.DescribeFunction("MCPBridge.Discovery.Tests.Fixtures.Widget.Describe", memberId: null);

        Assert.Null(result.Single);
        Assert.NotNull(result.Overloads);
        Assert.Equal("MCPBridge.Discovery.Tests.Fixtures.Widget.Describe", result.Overloads!.Member);
        Assert.Equal(2, result.Overloads.Overloads.Count);
        Assert.All(result.Overloads.Overloads, o => Assert.Contains("Describe", o.Signature));
    }

    [Fact]
    public void DescribeFunction_MemberIdOnly_ResolvesMethodOverload_MatchesMemberPlusMemberIdResult()
    {
        // member_id alone (M: prefix, method) resolves exactly the overload it names, with no "member" at
        // all -- must match the result of passing member+member_id together for the same member_id.
        var service = NewService();
        const string fullName = FixturesNamespace + ".Widget.Describe";

        var overloads = service.DescribeFunction(fullName, memberId: null).Overloads!.Overloads;
        var target = overloads[0];
        Assert.StartsWith("M:", target.MemberId);

        var byMemberAndId = service.DescribeFunction(fullName, memberId: target.MemberId);
        var byIdOnly = service.DescribeFunction(member: null, memberId: target.MemberId);

        Assert.NotNull(byIdOnly.Single);
        Assert.Equal(byMemberAndId.Single!.MemberId, byIdOnly.Single!.MemberId);
        Assert.Equal(byMemberAndId.Single.Signature, byIdOnly.Single.Signature);
    }

    [Fact]
    public void DescribeFunction_MemberIdOnly_ResolvesProperty()
    {
        // member_id alone (P: prefix) -- Widget.Id has no overloads, so the member-based lookup itself
        // already returns Single; this just confirms the same member_id resolves with member entirely
        // absent.
        var service = NewService();

        var byMember = service.DescribeFunction(FixturesNamespace + ".Widget.Id", memberId: null);
        Assert.NotNull(byMember.Single);
        var propertyMemberId = byMember.Single!.MemberId;
        Assert.StartsWith("P:", propertyMemberId);

        var byIdOnly = service.DescribeFunction(member: null, memberId: propertyMemberId);

        Assert.NotNull(byIdOnly.Single);
        Assert.Equal("Id", byIdOnly.Single!.Name);
        Assert.Equal("Property", byIdOnly.Single.Kind);
    }

    [Fact]
    public void DescribeFunction_MemberIdOnly_ResolvesConstructor()
    {
        // member_id alone, a "#ctor" member_id -- exercises isCtorRequest's memberName match, derived
        // purely from the member_id (ParseMemberId), with member entirely absent.
        var service = NewService();
        const string fullName = FixturesNamespace + ".Widget.ctor";

        var overloads = service.DescribeFunction(fullName, memberId: null).Overloads!.Overloads;
        var target = overloads[0];
        Assert.Contains("#ctor", target.MemberId);

        var byIdOnly = service.DescribeFunction(member: null, memberId: target.MemberId);

        Assert.NotNull(byIdOnly.Single);
        Assert.Equal("Constructor", byIdOnly.Single!.Kind);
    }

    [Fact]
    public void DescribeFunction_MemberIdOnly_ResolvesGenericMethod()
    {
        // A generic method's member_id carries a "``N" arity suffix that the reflected member's Name
        // does not have, so member_id-only resolution must strip it -- otherwise no candidate matches and
        // every generic method is unreachable by member_id alone. GenericHolder.Read has a generic and a
        // non-generic member sharing the arity-stripped name, so the member_id has to pick between them.
        var service = NewService();
        const string fullName = FixturesNamespace + ".GenericHolder.Read";

        var overloads = service.DescribeFunction(fullName, memberId: null).Overloads!.Overloads;
        var generic = overloads.Single(o => o.MemberId.Contains("``1"));
        var nonGeneric = overloads.Single(o => !o.MemberId.Contains("``1"));

        var byGenericId = service.DescribeFunction(member: null, memberId: generic.MemberId);
        var byNonGenericId = service.DescribeFunction(member: null, memberId: nonGeneric.MemberId);

        Assert.NotNull(byGenericId.Single);
        Assert.Equal(generic.MemberId, byGenericId.Single!.MemberId);
        Assert.Equal("Read", byGenericId.Single.Name);

        // The member_id selects a specific one of the two, rather than always yielding the same member.
        Assert.NotNull(byNonGenericId.Single);
        Assert.Equal(nonGeneric.MemberId, byNonGenericId.Single!.MemberId);
        Assert.NotEqual(byGenericId.Single.MemberId, byNonGenericId.Single.MemberId);
    }

    [Fact]
    public void DescribeFunction_InheritedMember_MemberAndMemberIdNameDifferentTypes_StillResolves()
    {
        // Pins the deliberate absence of a member/member_id cross-check (issue #64). Bolt inherits
        // Tighten from Fastener without overriding it, so the two arguments legitimately name DIFFERENT
        // types: member is the type the caller queried, member_id the type that DECLARES the member. A
        // "do these agree" check would reject this, which is why DescribeFunction deliberately has none.
        var service = NewService();

        var byDeclaringType = service.DescribeFunction(FixturesNamespace + ".Fastener.Tighten", memberId: null);
        var declaredMemberId = byDeclaringType.Single!.MemberId;
        Assert.Contains(".Fastener.Tighten", declaredMemberId);

        // The queried type (Bolt) disagrees with the declaring type named in the member_id (Fastener).
        var viaDerivedType = service.DescribeFunction(FixturesNamespace + ".Bolt.Tighten", memberId: declaredMemberId);

        Assert.NotNull(viaDerivedType.Single);
        Assert.Equal(declaredMemberId, viaDerivedType.Single!.MemberId);
        Assert.Equal("Tighten", viaDerivedType.Single.Name);
    }

    [Fact]
    public void DescribeFunction_MemberIdOnly_Malformed_ThrowsNotFound()
    {
        // No resolvable "Type.Member" shape once the (absent) kind prefix and (absent) parameter list are
        // stripped -- no '.' left to split on.
        var service = NewService();

        Assert.Throws<DiscoveryMemberNotFoundException>(() =>
            service.DescribeFunction(member: null, memberId: "not-a-valid-member-id"));
    }

    [Fact]
    public void DescribeFunction_MemberIdOnly_UnknownType_ThrowsNotFound()
    {
        var service = NewService();

        Assert.Throws<DiscoveryMemberNotFoundException>(() =>
            service.DescribeFunction(member: null, memberId: "M:" + FixturesNamespace + ".NoSuchType.Foo"));
    }

    [Fact]
    public void DescribeFunction_MemberIdOnly_DoesNotMatchAnyCandidate_ThrowsNotFound()
    {
        // The type and member name both resolve (Widget.Describe exists), but no candidate's own
        // member_id equals this exact string (wrong parameter list) -- the member_id-era equivalent of the
        // old out-of-range overload_index case.
        var service = NewService();

        Assert.Throws<DiscoveryMemberNotFoundException>(() =>
            service.DescribeFunction(member: null, memberId: "M:" + FixturesNamespace + ".Widget.Describe(System.String)"));
    }

    [Fact]
    public void DescribeFunction_ParameterDescriptionsJoinedFromXmlDoc()
    {
        var service = NewService();
        const string fullName = FixturesNamespace + ".Widget.Describe";

        var overloads = service.DescribeFunction(fullName, memberId: null).Overloads!.Overloads;
        var withParam = overloads.First(o => o.MemberId.Contains('('));
        var result = service.DescribeFunction(fullName, memberId: withParam.MemberId);

        var param = Assert.Single(result.Single!.Parameters);
        Assert.Equal("detailLevel", param.Name);
        Assert.Equal("How much detail to include.", param.Description);
    }

    [Fact]
    public void DescribeFunction_UnknownMember_ThrowsNotFound()
    {
        var service = NewService();

        Assert.Throws<DiscoveryMemberNotFoundException>(() =>
            service.DescribeFunction("MCPBridge.Discovery.Tests.Fixtures.Widget.NoSuchMethod", null));
    }

    [Fact]
    public void DescribeFunction_UnknownType_ThrowsNotFound()
    {
        var service = NewService();

        Assert.Throws<DiscoveryMemberNotFoundException>(() =>
            service.DescribeFunction("MCPBridge.Discovery.Tests.Fixtures.NoSuchType.Foo", null));
    }

    [Fact]
    public void DescribeFunction_NeitherMemberNorMemberId_ThrowsJsonRpcParamException()
    {
        var service = NewService();

        Assert.Throws<JsonRpcParamException>(() =>
            service.DescribeFunction(member: null, memberId: null));
    }

    [Fact]
    public void DescribeFunction_Constructor_ResolvesViaCtorKeyword()
    {
        var service = NewService();

        var result = service.DescribeFunction("MCPBridge.Discovery.Tests.Fixtures.Widget.ctor", memberId: null);

        // Widget has two constructors -> ambiguous without disambiguation.
        Assert.NotNull(result.Overloads);
        Assert.Equal(2, result.Overloads!.Overloads.Count);
    }

    [Fact]
    public void SearchFunctions_ExactScoreTie_PutsTheCallableMemberFirst()
    {
        // 3rd review round, measured on the real corpus: "set the parameter of an element" tied
        // Parameter.Element and Element.Parameter (properties) with Parameter.Set (a method) at 658.2 and
        // ordered them by name, so the properties took ranks 2-5 and the method the query actually
        // describes landed at 6. Ties this exact are common -- several members of one type routinely
        // explain a query equally well -- so the fallback ordering matters.
        //
        // Fixtures.Tie exists for this: the query matches only its TYPE name, so both members score
        // identically by construction and the tie-break is the only thing that can separate them. The
        // property is named to sort first alphabetically, so a pass here cannot be alphabetical ordering
        // getting lucky.
        var service = NewService();

        var result = service.SearchFunctions("tie", namespaceFilter: null, cursor: null, topN: 10);
        var ordered = result.Results.ToList();
        var methodAt = ordered.FindIndex(r => r.Member.Kind == "Method" && r.Member.DeclaringType.EndsWith(".Tie", StringComparison.Ordinal));
        var propertyAt = ordered.FindIndex(r => r.Member.Kind == "Property" && r.Member.DeclaringType.EndsWith(".Tie", StringComparison.Ordinal));

        Assert.True(methodAt >= 0 && propertyAt >= 0, "both Tie members must be in the results");
        Assert.Equal(ordered[methodAt].Score, ordered[propertyAt].Score, precision: 9);
        Assert.True(
            methodAt < propertyAt,
            $"the method must outrank the property on an exact tie; got method at {methodAt}, property at {propertyAt}");
    }
}
