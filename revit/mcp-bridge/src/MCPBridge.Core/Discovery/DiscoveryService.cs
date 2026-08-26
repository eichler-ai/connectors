using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MCPBridge.Core.Protocol;

namespace MCPBridge.Core.Discovery;

/// <summary>
/// The live-reflection engine behind PRD §08's three discovery commands. Reflects directly over the
/// <see cref="Assembly"/> objects handed in via <see cref="DiscoveryOptions"/> (never loads anything from
/// disk itself beyond each assembly's XML doc sidecar, located via
/// <c>Path.ChangeExtension(assembly.Location, ".xml")</c>) -- see this type's own construction for why that
/// convention, not a guessed install path, is what makes this correct against whatever Revit version is
/// actually running.
///
/// <para>
/// Deliberately synchronous and side-effect-free: no <c>Document</c>/<c>UIApplication</c> touched anywhere
/// in this file, so callers (RequestDispatcher) can and must serve these three commands directly on the
/// connection thread, never through ExternalEvent/ExecutionManager (PRD §08's execution-locus decision).
/// </para>
/// </summary>
public sealed class DiscoveryService
{
    private const int MaxSummaryLength = 300;

    private readonly IReadOnlyList<Assembly> _assemblies;
    private readonly IReadOnlyList<Type> _publicTypes;

    public DiscoveryService(DiscoveryOptions options)
    {
        _assemblies = options.Assemblies;

        var docIndexes = new List<XmlDocIndex>();
        foreach (var assembly in _assemblies)
        {
            XmlDocIndex index;
            try
            {
                index = string.IsNullOrEmpty(assembly.Location)
                    ? XmlDocIndex.Empty
                    : XmlDocIndex.LoadFromFile(Path.ChangeExtension(assembly.Location, ".xml"));
            }
            catch
            {
                index = XmlDocIndex.Empty;
            }

            docIndexes.Add(index);
        }

        _docIndexes = docIndexes;

        var types = new List<Type>();
        foreach (var assembly in _assemblies)
        {
            Type[] assemblyTypes;
            try
            {
                assemblyTypes = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                assemblyTypes = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }

            foreach (var t in assemblyTypes)
            {
                if (IsPubliclyVisible(t))
                {
                    types.Add(t);
                }
            }
        }

        _publicTypes = types.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
    }

    // Parallel to _assemblies -- the doc index for _assemblies[i] is _docIndexes[i]. Kept as a plain list
    // rather than a Dictionary<Assembly,...> since assemblies compare by reference identity fine either way
    // and this avoids a redundant lookup structure for what's always a tiny (2-ish) list.
    private readonly IReadOnlyList<XmlDocIndex> _docIndexes;

    // -------------------------------------------------------------------------------------------------
    // list_functions
    // -------------------------------------------------------------------------------------------------

    public ListFunctionsResult ListFunctions(string? namespaceFilter, string? typeFilter, string? cursor, int pageSize)
    {
        List<MemberSignature> scoped;

        if (!string.IsNullOrEmpty(typeFilter))
        {
            var type = FindType(typeFilter);
            scoped = type is null
                ? new List<MemberSignature>()
                : GetPublicMembers(type)
                    .OrderBy(m => m.Name, StringComparer.Ordinal)
                    .ThenBy(m => ParamCountOf(m), StringComparer.Ordinal)
                    .ToList();
        }
        else if (!string.IsNullOrEmpty(namespaceFilter))
        {
            scoped = _publicTypes
                .Where(t => t.Namespace == namespaceFilter)
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .SelectMany(GetPublicMembers)
                .OrderBy(m => m.DeclaringType, StringComparer.Ordinal)
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .ThenBy(m => ParamCountOf(m), StringComparer.Ordinal)
                .ToList();
        }
        else
        {
            // No scope at all: PRD §08 discourages this (multi-megabyte unscoped surface) but must not
            // hard-fail -- return the (large, paginated) type list so the agent can narrow from there.
            scoped = _publicTypes.Select(BuildTypeMemberSignature).ToList();
        }

        var offset = ParseCursor(cursor);
        var page = scoped.Skip(offset).Take(pageSize).ToList();
        var nextOffset = offset + page.Count;

        return new ListFunctionsResult
        {
            Members = page,
            NextCursor = nextOffset < scoped.Count ? nextOffset.ToString() : null,
            TotalScoped = scoped.Count,
        };
    }

    // -------------------------------------------------------------------------------------------------
    // search_functions
    // -------------------------------------------------------------------------------------------------

    public SearchFunctionsResult SearchFunctions(string query, string? cursor, int topN)
    {
        var queryLower = query.Trim().ToLowerInvariant();
        var queryTokens = queryLower.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);

        var scored = new List<ScoredMember>();
        foreach (var type in _publicTypes)
        {
            foreach (var member in GetPublicMembers(type))
            {
                var score = ScoreMember(member, queryLower, queryTokens);
                if (score > 0)
                {
                    scored.Add(new ScoredMember { Member = member, Score = score });
                }
            }
        }

        var ranked = scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Member.Name, StringComparer.Ordinal)
            .ThenBy(s => s.Member.MemberId, StringComparer.Ordinal)
            .ToList();

        var offset = ParseCursor(cursor);
        var page = ranked.Skip(offset).Take(topN).ToList();
        var nextOffset = offset + page.Count;

        return new SearchFunctionsResult
        {
            Results = page,
            NextCursor = nextOffset < ranked.Count ? nextOffset.ToString() : null,
            TotalMatched = ranked.Count,
        };
    }

    /// <summary>
    /// Simple, deterministic, defensible ranking (PRD §08: doesn't need to be state-of-the-art): exact
    /// name match scores highest, substring name match next, otherwise a token-overlap score against
    /// name+summary combined text. Zero overlap means "not a match" (score 0, excluded).
    /// </summary>
    private static double ScoreMember(MemberSignature member, string queryLower, string[] queryTokens)
    {
        var nameLower = member.Name.ToLowerInvariant();
        if (nameLower == queryLower)
        {
            return 100;
        }

        if (nameLower.Contains(queryLower, StringComparison.Ordinal))
        {
            return 70;
        }

        if (queryTokens.Length == 0)
        {
            return 0;
        }

        var haystack = (nameLower + " " + (member.Summary ?? "")).ToLowerInvariant();
        var matched = queryTokens.Count(tok => haystack.Contains(tok, StringComparison.Ordinal));
        return matched == 0 ? 0 : 50.0 * matched / queryTokens.Length;
    }

    // -------------------------------------------------------------------------------------------------
    // describe_function
    // -------------------------------------------------------------------------------------------------

    public DescribeFunctionResult DescribeFunction(string member, int? overloadIndex, string? memberId)
    {
        var lastDot = member.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == member.Length - 1)
        {
            throw new DiscoveryMemberNotFoundException(
                $"'{member}' is not a valid dotted Namespace.Type.MemberName member reference.");
        }

        var typeName = member[..lastDot];
        var memberName = member[(lastDot + 1)..];

        var type = FindType(typeName);
        if (type is null)
        {
            throw new DiscoveryMemberNotFoundException($"no type '{typeName}' found (from member reference '{member}').");
        }

        var allMembers = GetPublicMembersWithReflection(type);
        var isCtorRequest = memberName is "ctor" or "#ctor" or ".ctor";
        var candidates = allMembers
            .Where(m => isCtorRequest ? m.Info is ConstructorInfo : m.Info.Name == memberName)
            .ToList();
        if (candidates.Count == 0)
        {
            throw new DiscoveryMemberNotFoundException($"no public member named '{memberName}' found on type '{typeName}'.");
        }

        (MemberInfo Info, MemberSignature Signature)? resolved = null;

        if (!string.IsNullOrEmpty(memberId))
        {
            resolved = allMembers.FirstOrDefault(m => m.Signature.MemberId == memberId);
            if (resolved is null)
            {
                throw new DiscoveryMemberNotFoundException($"member_id '{memberId}' does not resolve to any public member on type '{typeName}'.");
            }
        }
        else if (overloadIndex is { } idx)
        {
            var ordered = candidates.OrderBy(m => m.Signature.Signature, StringComparer.Ordinal).ToList();
            if (idx < 0 || idx >= ordered.Count)
            {
                throw new DiscoveryMemberNotFoundException(
                    $"overload_index {idx} is out of range for '{member}' (has {ordered.Count} overload(s)).");
            }

            resolved = ordered[idx];
        }
        else if (candidates.Count == 1)
        {
            resolved = candidates[0];
        }
        else
        {
            // Ambiguous, no disambiguation given: compact overload list (PRD §08).
            var overloads = candidates
                .OrderBy(m => m.Signature.Signature, StringComparer.Ordinal)
                .Select(m => new DescribeOverloadEntry { MemberId = m.Signature.MemberId, Signature = m.Signature.Signature })
                .ToList();

            return DescribeFunctionResult.FromOverloads(new DescribeFunctionOverloadList { Member = member, Overloads = overloads });
        }

        var (info, sig) = resolved.Value;
        var docIndex = DocIndexFor(type);
        docIndex.TryGet(sig.MemberId, out var docEntry);

        var parameters = GetParameters(info)
            .Select(p => new DescribeParameter
            {
                Name = p.Name ?? "",
                Type = SignatureFormatter.ParamTypeName(p.ParameterType),
                Description = docEntry?.Parameters.TryGetValue(p.Name ?? "", out var desc) == true ? desc : null,
            })
            .ToList();

        var single = new DescribeFunctionSingle
        {
            MemberId = sig.MemberId,
            Kind = sig.Kind,
            Namespace = sig.Namespace,
            DeclaringType = sig.DeclaringType,
            Name = sig.Name,
            Signature = sig.Signature,
            Summary = docEntry?.Summary,
            Parameters = parameters,
            Returns = docEntry?.Returns,
            OverloadCount = candidates.Count,
        };

        return DescribeFunctionResult.FromSingle(single);
    }

    private static ParameterInfo[] GetParameters(MemberInfo info) => info switch
    {
        MethodInfo mi => mi.GetParameters(),
        ConstructorInfo ci => ci.GetParameters(),
        PropertyInfo pi => pi.GetIndexParameters(),
        _ => Array.Empty<ParameterInfo>(),
    };

    // -------------------------------------------------------------------------------------------------
    // Shared reflection/join plumbing
    // -------------------------------------------------------------------------------------------------

    private Type? FindType(string fullyQualifiedName) =>
        _publicTypes.FirstOrDefault(t => t.FullName == fullyQualifiedName || t.FullName?.Replace('+', '.') == fullyQualifiedName);

    private static bool IsPubliclyVisible(Type t) =>
        (t.IsPublic || t.IsNestedPublic) && !IsCompilerGenerated(t);

    private static bool IsCompilerGenerated(MemberInfo m) =>
        m.GetCustomAttribute(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)) is not null;

    private List<MemberSignature> GetPublicMembers(Type type) => GetPublicMembersWithReflection(type).Select(m => m.Signature).ToList();

    private List<(MemberInfo Info, MemberSignature Signature)> GetPublicMembersWithReflection(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var docIndex = DocIndexFor(type);
        var results = new List<(MemberInfo, MemberSignature)>();

        foreach (var ci in type.GetConstructors(flags))
        {
            results.Add((ci, BuildMemberSignature(ci, "Constructor", docIndex)));
        }

        foreach (var mi in type.GetMethods(flags))
        {
            if (mi.IsSpecialName || IsCompilerGenerated(mi))
            {
                continue; // property/event accessors (get_/set_/add_/remove_) and operator overloads.
            }

            results.Add((mi, BuildMemberSignature(mi, "Method", docIndex)));
        }

        foreach (var pi in type.GetProperties(flags))
        {
            if (IsCompilerGenerated(pi))
            {
                continue;
            }

            results.Add((pi, BuildMemberSignature(pi, "Property", docIndex)));
        }

        foreach (var fi in type.GetFields(flags))
        {
            if (fi.IsSpecialName || IsCompilerGenerated(fi))
            {
                continue; // e.g. auto-property backing fields.
            }

            results.Add((fi, BuildMemberSignature(fi, "Field", docIndex)));
        }

        foreach (var ei in type.GetEvents(flags))
        {
            if (IsCompilerGenerated(ei))
            {
                continue;
            }

            results.Add((ei, BuildMemberSignature(ei, "Event", docIndex)));
        }

        return results;
    }

    private MemberSignature BuildMemberSignature(MemberInfo info, string kind, XmlDocIndex docIndex)
    {
        var declaringType = info.DeclaringType!;
        var memberId = XmlDocId.GetDocId(info);
        docIndex.TryGet(memberId, out var docEntry);

        return new MemberSignature
        {
            MemberId = memberId,
            Kind = kind,
            Namespace = declaringType.Namespace ?? "",
            DeclaringType = (declaringType.FullName ?? declaringType.Name).Replace('+', '.'),
            Name = info is ConstructorInfo ? declaringType.Name : info.Name,
            Signature = SignatureFormatter.BuildSignature(info),
            Summary = Truncate(docEntry?.Summary),
        };
    }

    private MemberSignature BuildTypeMemberSignature(Type t)
    {
        var docIndex = DocIndexFor(t);
        var memberId = XmlDocId.GetDocId(t);
        docIndex.TryGet(memberId, out var docEntry);

        return new MemberSignature
        {
            MemberId = memberId,
            Kind = "Type",
            Namespace = t.Namespace ?? "",
            DeclaringType = (t.FullName ?? t.Name).Replace('+', '.'),
            Name = t.Name,
            Signature = SignatureFormatter.BuildSignature(t),
            Summary = Truncate(docEntry?.Summary),
        };
    }

    private XmlDocIndex DocIndexFor(Type t)
    {
        for (var i = 0; i < _assemblies.Count; i++)
        {
            if (ReferenceEquals(_assemblies[i], t.Assembly))
            {
                return _docIndexes[i];
            }
        }

        return XmlDocIndex.Empty;
    }

    private static string? Truncate(string? summary)
    {
        if (string.IsNullOrEmpty(summary) || summary.Length <= MaxSummaryLength)
        {
            return summary;
        }

        return summary[..MaxSummaryLength].TrimEnd() + "...";
    }

    private static string ParamCountOf(MemberSignature m) => m.Signature.Count(c => c == ',').ToString("D4");

    /// <summary>Parses a list_functions/search_functions cursor (an opaque offset-into-the-sorted-list decimal string) defensively -- an invalid/non-numeric cursor is a clear params error, not a crash.</summary>
    private static int ParseCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return 0;
        }

        if (!int.TryParse(cursor, out var offset) || offset < 0)
        {
            throw new JsonRpcParamException($"params.cursor '{cursor}' is not a valid non-negative integer offset.");
        }

        return offset;
    }
}
