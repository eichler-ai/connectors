using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MCPBridge.Core.Protocol;

namespace MCPBridge.Core.Discovery;

/// <summary>
/// The query facade behind PRD §08's three discovery commands (RequestDispatcher's own seam: unchanged
/// method shapes -- <see cref="ListFunctions"/>/<see cref="SearchFunctions"/>/<see cref="DescribeFunction"/>
/// -- across the move to a persistent cache). Composes <see cref="DiscoveryCache"/> for all actual
/// data access; this class owns only pagination/cursor validation, param validation, and describe_function's
/// overload-disambiguation policy -- the same responsibilities it always had, just reading from SQLite
/// instead of reflecting fresh on every call.
///
/// <para>
/// Deliberately synchronous and side-effect-free: no <c>Document</c>/<c>UIApplication</c> touched anywhere
/// in this file, so callers (RequestDispatcher) can and must serve these three commands directly on the
/// connection thread, never through ExternalEvent/ExecutionManager (PRD §08's execution-locus decision).
/// </para>
/// </summary>
public sealed class DiscoveryService
{
    private readonly DiscoveryCache _cache;

    public DiscoveryService(DiscoveryCache cache)
    {
        _cache = cache;
    }

    // -------------------------------------------------------------------------------------------------
    // list_functions -- strict one-level-at-a-time tree (PRD §08 addendum, explicit user design decision):
    // no args -> namespaces; +namespace -> types in it; +namespace+type -> members of it. Never a flat
    // cross-scope member dump.
    // -------------------------------------------------------------------------------------------------

    public ListFunctionsResult ListFunctions(string? namespaceFilter, string? typeFilter, string? cursor, int pageSize)
    {
        if (!string.IsNullOrEmpty(typeFilter) && string.IsNullOrEmpty(namespaceFilter))
        {
            throw new JsonRpcParamException(
                "params.type_name requires params.namespace -- list_functions is a strict one-level-at-a-time tree (namespaces -> types -> members); scope by namespace first, then narrow by type.");
        }

        if (!string.IsNullOrEmpty(typeFilter))
        {
            // params.type_name is documented (and the members tier's own wire "type" field is displayed)
            // as a bare, prefix-stripped name -- e.g. "Wall", not "Autodesk.Revit.DB.Wall" -- matching the
            // same convention the types tier already uses. Accept a fully-qualified value too (a caller
            // that copies a type name straight out of the types tier's own namespace-scoped listing, or
            // out of the tool's own jsonschema example, shouldn't get a silent empty result over it) by
            // stripping the given namespace's own prefix if present.
            var bareTypeName = typeFilter.StartsWith(namespaceFilter + ".", StringComparison.Ordinal)
                ? typeFilter[(namespaceFilter!.Length + 1)..]
                : typeFilter;
            var members = _cache.ListMemberNames(namespaceFilter!, bareTypeName);
            return BuildTierResult(ListFunctionsTier.Members, namespaceFilter, bareTypeName, members, cursor, pageSize, "lf:members:" + namespaceFilter + "." + bareTypeName);
        }

        if (!string.IsNullOrEmpty(namespaceFilter))
        {
            var types = _cache.ListTypeNames(namespaceFilter);
            return BuildTierResult(ListFunctionsTier.Types, namespaceFilter, null, types, cursor, pageSize, "lf:types:" + namespaceFilter);
        }

        const string scopeKey = "lf:namespaces";
        var namespaces = _cache.ListNamespaces();
        var offset = ParseCursor(cursor, scopeKey);
        var page = namespaces.Skip(offset).Take(pageSize).ToList();
        var nextOffset = offset + page.Count;

        return new ListFunctionsResult
        {
            Tier = ListFunctionsTier.Namespaces,
            Names = page.Select(n => n.Namespace).ToList(),
            Counts = page.Select(n => n.TypeCount).ToList(),
            NextCursor = nextOffset < namespaces.Count ? BuildCursor(nextOffset, scopeKey) : null,
            TotalScoped = namespaces.Count,
        };
    }

    private static ListFunctionsResult BuildTierResult(
        ListFunctionsTier tier, string? namespaceFilter, string? typeFilter, IReadOnlyList<string> names, string? cursor, int pageSize, string scopeKey)
    {
        var offset = ParseCursor(cursor, scopeKey);
        var page = names.Skip(offset).Take(pageSize).ToList();
        var nextOffset = offset + page.Count;

        return new ListFunctionsResult
        {
            Tier = tier,
            Namespace = namespaceFilter,
            TypeName = typeFilter,
            Names = page,
            NextCursor = nextOffset < names.Count ? BuildCursor(nextOffset, scopeKey) : null,
            TotalScoped = names.Count,
        };
    }

    // -------------------------------------------------------------------------------------------------
    // search_functions
    // -------------------------------------------------------------------------------------------------

    public SearchFunctionsResult SearchFunctions(string query, string? namespaceFilter, string? cursor, int topN)
    {
        var scored = _cache.Search(query, namespaceFilter);
        var ranked = scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Member.Name, StringComparer.Ordinal)
            .ThenBy(s => s.Member.MemberId, StringComparer.Ordinal)
            .ToList();

        var scopeKey = "sf:" + (namespaceFilter ?? "") + ":" + query.Trim().ToLowerInvariant();
        var offset = ParseCursor(cursor, scopeKey);
        var page = ranked.Skip(offset).Take(topN).ToList();
        var nextOffset = offset + page.Count;

        return new SearchFunctionsResult
        {
            Results = page.Select(r => new ScoredMember { Member = ToMemberSignature(r.Member), Score = r.Score }).ToList(),
            NextCursor = nextOffset < ranked.Count ? BuildCursor(nextOffset, scopeKey) : null,
            TotalMatched = ranked.Count,
        };
    }

    private static MemberSignature ToMemberSignature(DiscoveryMemberRow row) => new()
    {
        MemberId = row.MemberId,
        Kind = row.Kind,
        Namespace = row.Namespace,
        DeclaringType = row.DeclaringType,
        Name = row.Name,
        Signature = row.Signature,
        // list_functions/search_functions truncate; describe_function (below) doesn't -- see
        // DiscoveryReflector.MaxSummaryLength's own doc comment for why this happens here, not at
        // reflection/insert time.
        Summary = DiscoveryReflector.Truncate(row.Summary),
    };

    // -------------------------------------------------------------------------------------------------
    // describe_function
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// PRD §08 disambiguation contract. DECISION (issue #64): <c>overload_index</c> is gone -- it indexed an
    /// ordinal signature sort unrelated to search_functions' relevance ranking, so an agent that trusted
    /// search's own ordering could silently get the wrong overload. <c>member_id</c> is the only remaining
    /// disambiguator, and it is a reliable one: taken alone, it resolves a specific overload deterministically,
    /// with <paramref name="member"/> entirely optional in that case -- see <see cref="ParseMemberId"/> for how
    /// the type/member scope is derived from it.
    /// <para>
    /// <paramref name="member"/> alone (member_id absent) returns the ambiguous-overload list (<see
    /// cref="DescribeFunctionResult.Overloads"/>) whenever more than one candidate matches -- this is the sole
    /// remaining disambiguation mechanism, and every entry it returns carries its own member_id for a
    /// follow-up call.
    /// </para>
    /// <para>
    /// When BOTH are given, this method deliberately performs NO cross-check that they agree -- that is not an
    /// oversight. For an INHERITED member, <paramref name="member"/> names the type the caller actually
    /// queried (e.g. <c>Autodesk.Revit.DB.Wall.Dispose</c>) while <paramref name="memberId"/> names the type
    /// that DECLARES the member (e.g. <c>M:Autodesk.Revit.DB.Element.Dispose</c>) -- both legitimately describe
    /// the same resolved member, because candidates come from
    /// <see cref="DiscoveryCache.GetMembersIncludingInheritedByFullName"/>'s inherited-member union, not from
    /// an exact declaring-type match. Adding a "do these agree" check would break that legitimate case. A
    /// genuine mismatch is already loud without one: if member_id names something that isn't among member's
    /// own candidates, resolution falls straight through to <see cref="DiscoveryMemberNotFoundException"/>
    /// below -- there is no silent-retargeting hole here to close.
    /// </para>
    /// </summary>
    public DescribeFunctionResult DescribeFunction(string? member, string? memberId)
    {
        string typeName;
        string memberName;

        if (!string.IsNullOrEmpty(member))
        {
            var lastDot = member.LastIndexOf('.');
            if (lastDot <= 0 || lastDot == member.Length - 1)
            {
                throw new DiscoveryMemberNotFoundException(
                    $"'{member}' is not a valid dotted Namespace.Type.MemberName member reference.");
            }

            typeName = member[..lastDot];
            memberName = member[(lastDot + 1)..];
        }
        else if (!string.IsNullOrEmpty(memberId))
        {
            (typeName, memberName) = ParseMemberId(memberId);
        }
        else
        {
            throw new JsonRpcParamException(
                "params.member and params.member_id are both missing -- at least one is required to identify the member to describe.");
        }

        if (!_cache.TypeExistsByFullName(typeName))
        {
            throw new DiscoveryMemberNotFoundException($"no type '{typeName}' found (from member reference '{member ?? memberId}').");
        }

        var allMembers = _cache.GetMembersIncludingInheritedByFullName(typeName);
        var isCtorRequest = memberName is "ctor" or "#ctor" or ".ctor";
        var candidates = allMembers
            .Where(m => isCtorRequest ? m.Kind == "Constructor" : string.Equals(m.Name, memberName, StringComparison.Ordinal))
            .ToList();
        if (candidates.Count == 0)
        {
            throw new DiscoveryMemberNotFoundException($"no public member named '{memberName}' found on type '{typeName}'.");
        }

        DiscoveryMemberRow? resolved;

        if (!string.IsNullOrEmpty(memberId))
        {
            resolved = candidates.FirstOrDefault(m => m.MemberId == memberId);
            if (resolved is null)
            {
                throw new DiscoveryMemberNotFoundException($"member_id '{memberId}' does not resolve to any overload of '{memberName}' on type '{typeName}'.");
            }
        }
        else if (candidates.Count == 1)
        {
            resolved = candidates[0];
        }
        else
        {
            var overloads = candidates
                .OrderBy(m => m.Signature, StringComparer.Ordinal)
                .Select(m => new DescribeOverloadEntry { MemberId = m.MemberId, Signature = m.Signature })
                .ToList();

            // member is non-null here: this branch is only reachable with memberId empty (a non-empty one
            // either resolves or throws above), and an empty member with an empty memberId threw earlier.
            return DescribeFunctionResult.FromOverloads(new DescribeFunctionOverloadList { Member = member!, Overloads = overloads });
        }

        var resolvedRow = resolved!;
        var parameters = resolvedRow.Parameters
            .Select(p => new DescribeParameter { Name = p.Name, Type = p.Type, Description = p.Description })
            .ToList();

        var single = new DescribeFunctionSingle
        {
            MemberId = resolvedRow.MemberId,
            Kind = resolvedRow.Kind,
            Namespace = resolvedRow.Namespace,
            DeclaringType = resolvedRow.DeclaringType,
            Name = resolvedRow.Name,
            Signature = resolvedRow.Signature,
            Summary = resolvedRow.Summary,
            Parameters = parameters,
            Returns = resolvedRow.Returns,
            OverloadCount = candidates.Count,
        };

        return DescribeFunctionResult.FromSingle(single);
    }

    /// <summary>
    /// Derives (typeName, memberName) from a member_id alone, for the member-optional path (issue #64).
    /// XML-doc ids look like <c>M:Namespace.Type.Member</c>, <c>M:Namespace.Type.#ctor</c>,
    /// <c>P:Namespace.Type.Member</c>, or with a parameter-list suffix, <c>M:Namespace.Type.Member(ParamType)</c>
    /// (see <see cref="XmlDocId"/> for the id format this must invert). The parameter list is stripped from
    /// the FIRST '(' onward -- XML-doc ids use '{}' for generic arguments, never '()', so '(' is an
    /// unambiguous boundary here -- then the remainder is split on the LAST '.' into type and member.
    /// </summary>
    private static (string TypeName, string MemberName) ParseMemberId(string memberId)
    {
        var body = memberId;
        if (body.Length >= 2 && char.IsLetter(body[0]) && body[1] == ':')
        {
            body = body[2..];
        }

        var parenIndex = body.IndexOf('(');
        if (parenIndex >= 0)
        {
            body = body[..parenIndex];
        }

        var lastDot = body.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == body.Length - 1)
        {
            throw new DiscoveryMemberNotFoundException(
                $"member_id '{memberId}' is not a resolvable XML-doc id -- expected a shape like 'M:Namespace.Type.Member' or 'P:Namespace.Type.Member', optionally with a parameter-list suffix.");
        }

        return (body[..lastDot], body[(lastDot + 1)..]);
    }

    // -------------------------------------------------------------------------------------------------
    // Cursor plumbing (unchanged from the original in-memory DiscoveryService -- see its own review-finding
    // history: cursors embed a stable hash of the scope that produced them so replaying one against a
    // different namespace/type/query is a clear params error, not a silently-wrong page).
    // -------------------------------------------------------------------------------------------------

    private static string BuildCursor(int offset, string scopeKey) => $"{offset}:{HashScope(scopeKey)}";

    private static string HashScope(string scopeKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(scopeKey));
        return Convert.ToHexString(hash)[..8];
    }

    private static int ParseCursor(string? cursor, string scopeKey)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return 0;
        }

        var parts = cursor.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var offset) || offset < 0)
        {
            throw new JsonRpcParamException($"params.cursor '{cursor}' is not a valid cursor.");
        }

        if (!string.Equals(parts[1], HashScope(scopeKey), StringComparison.Ordinal))
        {
            throw new JsonRpcParamException($"params.cursor '{cursor}' was issued for a different query -- re-issue the original request without a cursor to start over, rather than reusing this one with different params.");
        }

        return offset;
    }
}
