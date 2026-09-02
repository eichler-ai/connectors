using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MCPBridge.Core.Discovery;

namespace MCPBridge.Core.Protocol;

/// <summary>
/// Serializes <see cref="DiscoveryService"/>'s list_functions/search_functions/describe_function results
/// into the wire response shapes PRD §08 (and the discovery feature's own task brief) specify verbatim --
/// modeled on <see cref="ExecutionResultMessage"/>'s pattern (a private DTO + Envelope +
/// <c>JsonSerializer.Serialize(..., WireJson.Compact)</c>), but with its own DTOs since discovery's result
/// shape has nothing in common with execute_script's.
/// </summary>
public static class DiscoveryResultMessage
{
    private class MemberDto
    {
        [JsonPropertyName("member_id")]
        public string MemberId { get; set; } = "";

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "";

        [JsonPropertyName("namespace")]
        public string Namespace { get; set; } = "";

        [JsonPropertyName("declaring_type")]
        public string DeclaringType { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = "";

        [JsonPropertyName("summary")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Summary { get; set; }
    }

    private sealed class ScoredMemberDto : MemberDto
    {
        [JsonPropertyName("score")]
        public double Score { get; set; }
    }

    private sealed class DumpedMemberDto : MemberDto
    {
        [JsonPropertyName("core")]
        public bool Core { get; set; }
    }

    private sealed class DumpMembersResultDto
    {
        [JsonPropertyName("members")]
        public List<DumpedMemberDto> Members { get; set; } = new();

        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("next_offset")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? NextOffset { get; set; }

        [JsonPropertyName("fingerprint")]
        public string Fingerprint { get; set; } = "";
    }

    private sealed class NamespaceEntryDto
    {
        [JsonPropertyName("namespace")]
        public string Namespace { get; set; } = "";

        [JsonPropertyName("type_count")]
        public int TypeCount { get; set; }
    }

    /// <summary>list_functions' no-args tier: namespace names only (PRD §08 addendum -- a strict one-level-at-a-time tree, never a flat member dump).</summary>
    private sealed class NamespaceListResultDto
    {
        [JsonPropertyName("namespaces")]
        public List<NamespaceEntryDto> Namespaces { get; set; } = new();

        [JsonPropertyName("next_cursor")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NextCursor { get; set; }

        [JsonPropertyName("total_scoped")]
        public int TotalScoped { get; set; }
    }

    /// <summary>list_functions' namespace-scoped tier: type names in that namespace, prefix-stripped, as one comma-separated string.</summary>
    private sealed class TypeListResultDto
    {
        [JsonPropertyName("namespace")]
        public string Namespace { get; set; } = "";

        [JsonPropertyName("types")]
        public string Types { get; set; } = "";

        [JsonPropertyName("next_cursor")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NextCursor { get; set; }

        [JsonPropertyName("total_scoped")]
        public int TotalScoped { get; set; }
    }

    /// <summary>list_functions' namespace+type-scoped tier: member names of that type, prefix-stripped, as one comma-separated string. describe_function is the only way to get full signature/summary/param detail on one of these.</summary>
    private sealed class MemberListResultDto
    {
        [JsonPropertyName("namespace")]
        public string Namespace { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("members")]
        public string Members { get; set; } = "";

        [JsonPropertyName("next_cursor")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NextCursor { get; set; }

        [JsonPropertyName("total_scoped")]
        public int TotalScoped { get; set; }
    }

    private sealed class SearchFunctionsResultDto
    {
        [JsonPropertyName("results")]
        public List<ScoredMemberDto> Results { get; set; } = new();

        [JsonPropertyName("next_cursor")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NextCursor { get; set; }

        [JsonPropertyName("total_matched")]
        public int TotalMatched { get; set; }
    }

    private sealed class DescribeParameterDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }
    }

    private sealed class DescribeSingleResultDto
    {
        [JsonPropertyName("member_id")]
        public string MemberId { get; set; } = "";

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "";

        [JsonPropertyName("namespace")]
        public string Namespace { get; set; } = "";

        [JsonPropertyName("declaring_type")]
        public string DeclaringType { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = "";

        [JsonPropertyName("summary")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Summary { get; set; }

        [JsonPropertyName("parameters")]
        public List<DescribeParameterDto> Parameters { get; set; } = new();

        [JsonPropertyName("returns")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Returns { get; set; }

        [JsonPropertyName("overload_count")]
        public int OverloadCount { get; set; }
    }

    private sealed class DescribeOverloadEntryDto
    {
        [JsonPropertyName("member_id")]
        public string MemberId { get; set; } = "";

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = "";
    }

    private sealed class DescribeOverloadListResultDto
    {
        [JsonPropertyName("member")]
        public string Member { get; set; } = "";

        [JsonPropertyName("overloads")]
        public List<DescribeOverloadEntryDto> Overloads { get; set; } = new();
    }

    private sealed class Envelope<TResult>
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public JsonElement Id { get; set; }

        [JsonPropertyName("result")]
        public TResult Result { get; set; } = default!;
    }

    public static string ListFunctions(JsonElement id, ListFunctionsResult result) => result.Tier switch
    {
        ListFunctionsTier.Namespaces => Serialize(id, new NamespaceListResultDto
        {
            Namespaces = result.Names
                .Zip(result.Counts ?? Enumerable.Repeat(0, result.Names.Count), (name, count) => new NamespaceEntryDto { Namespace = name, TypeCount = count })
                .ToList(),
            NextCursor = result.NextCursor,
            TotalScoped = result.TotalScoped,
        }),
        ListFunctionsTier.Types => Serialize(id, new TypeListResultDto
        {
            Namespace = result.Namespace ?? "",
            Types = string.Join(", ", result.Names),
            NextCursor = result.NextCursor,
            TotalScoped = result.TotalScoped,
        }),
        ListFunctionsTier.Members => Serialize(id, new MemberListResultDto
        {
            Namespace = result.Namespace ?? "",
            Type = result.TypeName ?? "",
            Members = string.Join(", ", result.Names),
            NextCursor = result.NextCursor,
            TotalScoped = result.TotalScoped,
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(result), result.Tier, "unknown list_functions tier"),
    };

    public static string SearchFunctions(JsonElement id, SearchFunctionsResult result)
    {
        var dto = new SearchFunctionsResultDto
        {
            Results = result.Results.Select(r =>
            {
                var scored = ToScoredMemberDto(r.Member);
                scored.Score = r.Score;
                return scored;
            }).ToList(),
            NextCursor = result.NextCursor,
            TotalMatched = result.TotalMatched,
        };

        return Serialize(id, dto);
    }

    /// <summary>dump_members (issue #107): {members:[member + core], total, next_offset?, fingerprint}.</summary>
    public static string DumpMembers(JsonElement id, DumpMembersResult result)
    {
        var dto = new DumpMembersResultDto
        {
            Members = result.Members.Select(m =>
            {
                var dto = ToMemberDto<DumpedMemberDto>(m.Member);
                dto.Core = m.IsCore;
                return dto;
            }).ToList(),
            Total = result.Total,
            NextOffset = result.NextOffset,
            Fingerprint = result.Fingerprint,
        };
        return Serialize(id, dto);
    }

    public static string DescribeFunction(JsonElement id, DescribeFunctionResult result)
    {
        if (result.Single is { } single)
        {
            var dto = new DescribeSingleResultDto
            {
                MemberId = single.MemberId,
                Kind = single.Kind,
                Namespace = single.Namespace,
                DeclaringType = single.DeclaringType,
                Name = single.Name,
                Signature = single.Signature,
                Summary = single.Summary,
                Parameters = single.Parameters.Select(p => new DescribeParameterDto { Name = p.Name, Type = p.Type, Description = p.Description }).ToList(),
                Returns = single.Returns,
                OverloadCount = single.OverloadCount,
            };

            return Serialize(id, dto);
        }

        var overloads = result.Overloads!;
        var overloadDto = new DescribeOverloadListResultDto
        {
            Member = overloads.Member,
            Overloads = overloads.Overloads.Select(o => new DescribeOverloadEntryDto { MemberId = o.MemberId, Signature = o.Signature }).ToList(),
        };

        return Serialize(id, overloadDto);
    }

    /// <summary>Populates the shared member fields of any <see cref="MemberDto"/> subclass -- one mapping for search_functions' scored rows and dump_members' rows alike.</summary>
    private static T ToMemberDto<T>(MemberSignature m) where T : MemberDto, new() => new()
    {
        MemberId = m.MemberId,
        Kind = m.Kind,
        Namespace = m.Namespace,
        DeclaringType = m.DeclaringType,
        Name = m.Name,
        Signature = m.Signature,
        Summary = m.Summary,
    };

    private static ScoredMemberDto ToScoredMemberDto(MemberSignature m) => ToMemberDto<ScoredMemberDto>(m);

    private static string Serialize<TResult>(JsonElement id, TResult dto)
    {
        var envelope = new Envelope<TResult> { Id = id, Result = dto };
        return JsonSerializer.Serialize(envelope, WireJson.Compact);
    }
}
