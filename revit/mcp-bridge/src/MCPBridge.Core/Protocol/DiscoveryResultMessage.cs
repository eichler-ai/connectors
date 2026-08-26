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

    private sealed class ListFunctionsResultDto
    {
        [JsonPropertyName("members")]
        public List<MemberDto> Members { get; set; } = new();

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

    public static string ListFunctions(JsonElement id, ListFunctionsResult result)
    {
        var dto = new ListFunctionsResultDto
        {
            Members = result.Members.Select(ToMemberDto).ToList(),
            NextCursor = result.NextCursor,
            TotalScoped = result.TotalScoped,
        };

        return Serialize(id, dto);
    }

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

    private static MemberDto ToMemberDto(MemberSignature m) => new()
    {
        MemberId = m.MemberId,
        Kind = m.Kind,
        Namespace = m.Namespace,
        DeclaringType = m.DeclaringType,
        Name = m.Name,
        Signature = m.Signature,
        Summary = m.Summary,
    };

    private static ScoredMemberDto ToScoredMemberDto(MemberSignature m) => new()
    {
        MemberId = m.MemberId,
        Kind = m.Kind,
        Namespace = m.Namespace,
        DeclaringType = m.DeclaringType,
        Name = m.Name,
        Signature = m.Signature,
        Summary = m.Summary,
    };

    private static string Serialize<TResult>(JsonElement id, TResult dto)
    {
        var envelope = new Envelope<TResult> { Id = id, Result = dto };
        return JsonSerializer.Serialize(envelope, WireJson.Compact);
    }
}
