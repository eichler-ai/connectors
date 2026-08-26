using System;
using System.Collections.Generic;

namespace MCPBridge.Core.Discovery;

/// <summary>Thrown by <see cref="DiscoveryService.DescribeFunction"/> when `member`/`member_id`/`overload_index` doesn't resolve to a real member -- a caller (RequestDispatcher) is expected to catch this and turn it into a JSON-RPC error with a remedy, same posture as <see cref="MCPBridge.Core.Protocol.JsonRpcParamException"/> for malformed params.</summary>
public sealed class DiscoveryMemberNotFoundException : Exception
{
    public DiscoveryMemberNotFoundException(string message) : base(message)
    {
    }
}

public sealed class ListFunctionsResult
{
    public required IReadOnlyList<MemberSignature> Members { get; init; }
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

/// <summary>describe_function's "compact overload list" wire shape -- returned when `member` is ambiguous and neither member_id nor overload_index was given to disambiguate.</summary>
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
