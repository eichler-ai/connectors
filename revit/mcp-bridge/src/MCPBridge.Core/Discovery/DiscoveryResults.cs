using System;
using System.Collections.Generic;

namespace MCPBridge.Core.Discovery;

/// <summary>Thrown by <see cref="DiscoveryService.DescribeFunction"/> when `member`/`member_id` doesn't resolve to a real member -- a caller (RequestDispatcher) is expected to catch this and turn it into a JSON-RPC error with a remedy, same posture as <see cref="MCPBridge.Core.Protocol.JsonRpcParamException"/> for malformed params.</summary>
public sealed class DiscoveryMemberNotFoundException : Exception
{
    public DiscoveryMemberNotFoundException(string message) : base(message)
    {
    }
}

/// <summary>list_functions' three scopes (PRD §08 addendum: a strict one-level-at-a-time tree, not a flat member dump) -- see <see cref="ListFunctionsResult"/>'s own doc comment for what each carries.</summary>
public enum ListFunctionsTier
{
    /// <summary>No params: every namespace name (with a documented-type count), nothing deeper.</summary>
    Namespaces,

    /// <summary>namespace given, no type: every documented type's short name in that namespace.</summary>
    Types,

    /// <summary>namespace + type given: every distinct member name (own + inherited) declared on that type.</summary>
    Members,
}

/// <summary>
/// list_functions' result, one tier at a time (PRD §08 addendum) -- deliberately NOT the old flat
/// "verbose member objects" shape: each tier returns just names (namespace names, or namespace/type-
/// prefix-stripped type/member names), leaving full per-member detail (signature/summary/params) to
/// describe_function alone. <see cref="Tier"/> says which of <see cref="Namespace"/>/<see cref="TypeName"/>/
/// <see cref="Counts"/> are meaningful for this particular result.
/// </summary>
public sealed class ListFunctionsResult
{
    public required ListFunctionsTier Tier { get; init; }

    /// <summary>Set for <see cref="ListFunctionsTier.Types"/> and <see cref="ListFunctionsTier.Members"/>.</summary>
    public string? Namespace { get; init; }

    /// <summary>Set for <see cref="ListFunctionsTier.Members"/> only.</summary>
    public string? TypeName { get; init; }

    /// <summary>Namespace names / type names / member names, per <see cref="Tier"/> -- already namespace- or type-prefix-stripped for the Types/Members tiers.</summary>
    public required IReadOnlyList<string> Names { get; init; }

    /// <summary>Documented-type count per entry, parallel to <see cref="Names"/> -- <see cref="ListFunctionsTier.Namespaces"/> only.</summary>
    public IReadOnlyList<int>? Counts { get; init; }

    public string? NextCursor { get; init; }
    public required int TotalScoped { get; init; }
}

/// <summary>MemberSignature plus the numeric relevance score search_functions' wire shape adds on top of the shared member object shape.</summary>
public sealed class ScoredMember
{
    public required MemberSignature Member { get; init; }
    public required double Score { get; init; }
}

public sealed class SearchFunctionsResult
{
    public required IReadOnlyList<ScoredMember> Results { get; init; }
    public string? NextCursor { get; init; }

    /// <summary>
    /// Size of the ranked, PAGEABLE set -- not the number of members in the corpus that matched.
    ///
    /// <para>Each tier caps how deep it keeps results (see <c>DiscoveryCache.TierCandidateLimit</c>), so on
    /// a broad query the true match count is far larger: "id" matches 4,768 members in the name-match tier
    /// alone and reports a few hundred here. This is deliberate and self-consistent -- the cursor walks
    /// exactly this many rows and no more -- but it is NOT the "how many things did you find" a caller
    /// might read it as.</para>
    ///
    /// <para>Issue #76 filed this as a defect ("total_matched lies"), on the theory that rows were being
    /// dropped in scan order and were therefore unreachable. The scan-order half is fixed: what each tier
    /// keeps is now its own top-by-score. The count remains a capped count, which is why the number MOVED
    /// when that fix landed (measured: "id" 926 to 881, "get" 843 to 827, "element" 869 to 843) without
    /// anything being lost -- tier 2's top-500-by-score simply overlaps tier 3's top-500-by-bm25 more than
    /// the old scan-order 500 did, so more rows dedup away. Raising the caps to make this an exact corpus
    /// count would mean materializing and ranking thousands of rows nobody pages to; documenting the
    /// meaning is the cheaper honest answer.</para>
    /// </summary>
    public required int TotalMatched { get; init; }
}

public sealed class DescribeParameter
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Description { get; init; }
}

/// <summary>describe_function's "single overload" wire shape -- the full doc entry for one resolved member.</summary>
public sealed class DescribeFunctionSingle
{
    public required string MemberId { get; init; }
    public required string Kind { get; init; }
    public required string Namespace { get; init; }
    public required string DeclaringType { get; init; }
    public required string Name { get; init; }
    public required string Signature { get; init; }
    public string? Summary { get; init; }
    public required IReadOnlyList<DescribeParameter> Parameters { get; init; }
    public string? Returns { get; init; }
    public required int OverloadCount { get; init; }
}

public sealed class DescribeOverloadEntry
{
    public required string MemberId { get; init; }
    public required string Signature { get; init; }
}

/// <summary>describe_function's "compact overload list" wire shape -- returned when `member` is ambiguous and member_id was not given to disambiguate.</summary>
public sealed class DescribeFunctionOverloadList
{
    public required string Member { get; init; }
    public required IReadOnlyList<DescribeOverloadEntry> Overloads { get; init; }
}

/// <summary>
/// Union of describe_function's two legal result shapes (PRD §08) -- exactly one of <see cref="Single"/>/
/// <see cref="Overloads"/> is set. <see cref="MCPBridge.Core.Protocol.DiscoveryResultMessage"/> serializes
/// whichever one is present.
/// </summary>
public sealed class DescribeFunctionResult
{
    public DescribeFunctionSingle? Single { get; init; }
    public DescribeFunctionOverloadList? Overloads { get; init; }

    public static DescribeFunctionResult FromSingle(DescribeFunctionSingle single) => new() { Single = single };
    public static DescribeFunctionResult FromOverloads(DescribeFunctionOverloadList overloads) => new() { Overloads = overloads };
}
