using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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

        // The narrower "browse/search" surface -- see _documentedTypes' own comment for why this is a
        // second list rather than a tighter filter on _publicTypes.
        var documented = new List<Type>();
        foreach (var t in _publicTypes)
        {
            var docIndex = DocIndexFor(t);
            if (ReferenceEquals(docIndex, XmlDocIndex.Empty) || docIndex.TryGet(XmlDocId.GetDocId(t), out _))
            {
                documented.Add(t);
            }
        }

        _documentedTypes = documented;
    }

    // The subset of _publicTypes that the assembly's own XML-doc sidecar has a "T:" entry for -- i.e. the
    // types Autodesk actually documents as API, not merely everything the C++/CLI build happened to emit as
    // public metadata. Used for the *browsing* paths only (unscoped list_functions, namespace-scoped
    // list_functions, and search_functions ranking), never for FindType: an explicit type_name/member lookup
    // still resolves against the full _publicTypes list, so this narrowing can hide a real-but-undocumented
    // type from browsing without ever making it unreachable.
    //
    // Escape hatch: an assembly with no (or an unparseable) sidecar yields XmlDocIndex.Empty, and for those
    // assemblies no doc filter is applied at all -- otherwise this list would be empty in every environment
    // without RevitAPI.xml (dev machines, MCPBridge.Core.Tests) and discovery would silently return nothing.
    private readonly IReadOnlyList<Type> _documentedTypes;

    // Parallel to _assemblies -- the doc index for _assemblies[i] is _docIndexes[i]. Kept as a plain list
    // rather than a Dictionary<Assembly,...> since assemblies compare by reference identity fine either way
    // and this avoids a redundant lookup structure for what's always a tiny (2-ish) list.
    private readonly IReadOnlyList<XmlDocIndex> _docIndexes;

    // -------------------------------------------------------------------------------------------------
    // list_functions
    // -------------------------------------------------------------------------------------------------

    public ListFunctionsResult ListFunctions(string? namespaceFilter, string? typeFilter, string? cursor, int pageSize)
    {
        // Review finding (L3): namespace + type_name together silently let type_name win, with no
        // guarantee the type is even in that namespace -- ambiguous input should be rejected, not
        // silently resolved one way.
        if (!string.IsNullOrEmpty(namespaceFilter) && !string.IsNullOrEmpty(typeFilter))
        {
            throw new JsonRpcParamException("params.namespace and params.type_name are mutually exclusive -- scope by one or the other, not both.");
        }

        List<MemberSignature> scoped;
        string scopeKey;

        if (!string.IsNullOrEmpty(typeFilter))
        {
            var type = FindType(typeFilter);
            // Review finding (H1): includes inherited members (e.g. Wall.Id, declared on Element), not
            // just this exact type's own declared members -- Revit's API is deeply inherited, and a
            // type-scoped list that only shows what the type itself declares misses most of what a
            // script actually calls on it.
            scoped = type is null
                ? new List<MemberSignature>()
                : GetMembersIncludingInherited(type)
                    .Select(m => m.Signature)
                    .OrderBy(m => m.Name, StringComparer.Ordinal)
                    .ThenBy(m => ParamCountOf(m), StringComparer.Ordinal)
                    .ToList();
            scopeKey = "lf:type:" + typeFilter;
        }
        else if (!string.IsNullOrEmpty(namespaceFilter))
        {
            scoped = _documentedTypes
                .Where(t => t.Namespace == namespaceFilter)
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .SelectMany(GetPublicMembers)
                .OrderBy(m => m.DeclaringType, StringComparer.Ordinal)
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .ThenBy(m => ParamCountOf(m), StringComparer.Ordinal)
                .ToList();
            scopeKey = "lf:ns:" + namespaceFilter;
        }
        else
        {
            // No scope at all: PRD §08 discourages this (multi-megabyte unscoped surface) but must not
            // hard-fail -- return the (large, paginated) type list so the agent can narrow from there.
            // _documentedTypes, not _publicTypes: this is the path where RevitAPI.dll's C++/CLI metadata
            // noise is most visible (PRD §08 describes ~1,700 types; raw public metadata is ~3x that).
            scoped = _documentedTypes.Select(BuildTypeMemberSignature).ToList();
            scopeKey = "lf:all";
        }

        var offset = ParseCursor(cursor, scopeKey);
        var page = scoped.Skip(offset).Take(pageSize).ToList();
        var nextOffset = offset + page.Count;

        return new ListFunctionsResult
        {
            Members = page,
            NextCursor = nextOffset < scoped.Count ? BuildCursor(nextOffset, scopeKey) : null,
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
        foreach (var type in _documentedTypes)
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

        var scopeKey = "sf:" + queryLower;
        var offset = ParseCursor(cursor, scopeKey);
        var page = ranked.Skip(offset).Take(topN).ToList();
        var nextOffset = offset + page.Count;

        return new SearchFunctionsResult
        {
            Results = page,
            NextCursor = nextOffset < ranked.Count ? BuildCursor(nextOffset, scopeKey) : null,
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

        // Review finding (H1): includes inherited members -- describe_function("...Wall.Id") must
        // resolve even though Id is declared on Element, not Wall itself.
        var allMembers = GetMembersIncludingInherited(type);
        var isCtorRequest = memberName is "ctor" or "#ctor" or ".ctor";
        var candidates = allMembers
            .Where(m => isCtorRequest ? m.Info is ConstructorInfo : m.Info.Name == memberName)
            .ToList();
        if (candidates.Count == 0)
        {
            throw new DiscoveryMemberNotFoundException($"no public member named '{memberName}' found on type '{typeName}'.");
        }

        // Review finding (M2): member_id and overload_index are two different ways to disambiguate the
        // same ambiguity -- accepting both silently discards one rather than erroring is exactly the
        // kind of "picked a value nobody actually asked for" behavior PRD §01 tries to avoid.
        if (!string.IsNullOrEmpty(memberId) && overloadIndex is not null)
        {
            throw new JsonRpcParamException("params.member_id and params.overload_index are mutually exclusive -- pick one way to disambiguate, not both.");
        }

        (MemberInfo Info, MemberSignature Signature)? resolved = null;

        if (!string.IsNullOrEmpty(memberId))
        {
            // Review finding (M2): search within `candidates` (already scoped to `memberName`), not
            // `allMembers` -- searching the whole type let member_id silently resolve to a DIFFERENT
            // member than the one named in `member` (e.g. member: "...Delete", member_id pointing at
            // "...Regenerate"), returning correct-shaped but wrong data with no error.
            resolved = candidates.FirstOrDefault(m => m.Signature.MemberId == memberId);
            if (resolved is null)
            {
                throw new DiscoveryMemberNotFoundException($"member_id '{memberId}' does not resolve to any overload of '{memberName}' on type '{typeName}'.");
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

    /// <summary>
    /// Whether a reflected type belongs on the API surface at all.
    ///
    /// <para>
    /// Uses <see cref="Type.IsVisible"/>, NOT <c>IsPublic || IsNestedPublic</c>: the latter is wrong for
    /// nested types, since <c>IsNestedPublic</c> is true for a <c>public</c> type nested inside an
    /// <c>internal</c> one, which is not externally visible at all. <c>IsVisible</c> walks the whole
    /// enclosing-type chain (and generic arguments) and is the framework's own definition of
    /// "externally visible".
    /// </para>
    ///
    /// <para>
    /// Also rejects types whose full name isn't shaped like a C# identifier path. RevitAPI.dll is a C++/CLI
    /// assembly, and its build emits native/ATL artifacts into public metadata with mangled names no C# API
    /// ever has -- e.g. <c>ATL.CTraceCategoryEx&lt;128,0&gt;.&lt;unnamed-type-TraceCategories&gt;</c>. A real
    /// C# type's <see cref="Type.FullName"/> only ever contains identifier characters plus '.', '+'
    /// (nesting) and '`' (generic arity), so anything outside that set is unambiguously not part of the
    /// API -- no risk of dropping a genuine member.
    /// </para>
    /// </summary>
    private static bool IsPubliclyVisible(Type t) =>
        t.IsVisible && !IsCompilerGenerated(t) && HasCSharpShapedName(t);

    private static bool HasCSharpShapedName(Type t)
    {
        var full = t.FullName;
        if (string.IsNullOrEmpty(full))
        {
            return false;
        }

        foreach (var c in full)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '+' || c == '`')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool IsCompilerGenerated(MemberInfo m) =>
        m.GetCustomAttribute(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)) is not null;

    private List<MemberSignature> GetPublicMembers(Type type) => GetPublicMembersWithReflection(type).Select(m => m.Signature).ToList();

    /// <summary>
    /// Review finding (H1): type_name-scoped list_functions and describe_function both need a type's
    /// FULL usable surface, including members declared on base types (Revit's API is deeply inherited --
    /// e.g. Wall.Id is declared on Element) -- <see cref="GetPublicMembersWithReflection"/> alone
    /// (BindingFlags.DeclaredOnly) only ever returns what the exact type itself declares. Walks the base
    /// chain, but ONLY through types that are themselves part of the discoverable surface (<see
    /// cref="_publicTypes"/>) -- this naturally stops at the System.Object/BCL boundary without hardcoding
    /// it, so object.ToString()/Equals()/etc. don't flood every type's member list. Deliberately NOT used
    /// for the namespace-scoped list_functions path or search_functions -- there it would multiply every
    /// type's inherited members across the whole namespace/search surface for no benefit (the base type is
    /// separately listed/searchable on its own).
    ///
    /// Deduplicates by (kind, name, parameter-type shape), most-derived first, so an override in a
    /// derived type is kept and the identical-shaped base declaration is dropped rather than both
    /// appearing as if they were two different overloads.
    /// </summary>
    private List<(MemberInfo Info, MemberSignature Signature)> GetMembersIncludingInherited(Type type)
    {
        var results = new List<(MemberInfo Info, MemberSignature Signature)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (Type? current = type; current is not null; current = current.BaseType)
        {
            if (current != type && !_publicTypes.Contains(current))
            {
                break;
            }

            foreach (var m in GetPublicMembersWithReflection(current))
            {
                var key = InheritanceDedupeKey(m.Info);
                if (seen.Add(key))
                {
                    results.Add(m);
                }
            }
        }

        return results;
    }

    private static string InheritanceDedupeKey(MemberInfo info)
    {
        var paramTypes = GetParameters(info).Select(p => p.ParameterType.FullName ?? p.ParameterType.Name);
        return info.MemberType + "|" + info.Name + "|" + string.Join(",", paramTypes);
    }

    // Review finding (M4): search_functions previously re-reflected every type's members from scratch on
    // every call (and every subsequent page of the same call), rebuilding ~50k MemberInfo/signature
    // objects each time against PRD §08's own "stay fast" requirement. A reflected type's own declared
    // members never change within this DiscoveryService instance's lifetime (one instance per Revit
    // connection, PRD §08's execution-locus section), so caching per type is safe and unconditional.
    private readonly Dictionary<Type, List<(MemberInfo Info, MemberSignature Signature)>> _memberCache = new();

    private List<(MemberInfo Info, MemberSignature Signature)> GetPublicMembersWithReflection(Type type)
    {
        if (_memberCache.TryGetValue(type, out var cached))
        {
            return cached;
        }

        var results = ReflectDeclaredMembers(type);
        _memberCache[type] = results;
        return results;
    }

    /// <summary>
    /// Review finding (H2): reflecting a single type's members can throw for a type with unresolvable
    /// references -- plausible in a C++/CLI interop assembly like RevitAPI.dll, which mixes genuine API
    /// types with native wrapper artifacts (see <see cref="IsPubliclyVisible"/>'s own doc comment). A
    /// single pathological type must not fail an entire list_functions/search_functions/describe_function
    /// call for every OTHER type -- skip just that type (empty member list), never propagate.
    /// </summary>
    private List<(MemberInfo Info, MemberSignature Signature)> ReflectDeclaredMembers(Type type)
    {
        try
        {
            return ReflectDeclaredMembersUnguarded(type);
        }
        catch
        {
            return new List<(MemberInfo, MemberSignature)>();
        }
    }

    private List<(MemberInfo Info, MemberSignature Signature)> ReflectDeclaredMembersUnguarded(Type type)
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
            // Review finding (L1): IsSpecialName is true for BOTH property/event accessors
            // (get_/set_/add_/remove_) AND operator overloads (op_Equality, op_Implicit, etc.) -- the
            // original blanket skip made every operator (e.g. ElementId's equality/conversion
            // operators, real, commonly-used API) permanently undiscoverable. Only skip the accessor
            // shapes, not op_*.
            if ((mi.IsSpecialName && !mi.Name.StartsWith("op_", StringComparison.Ordinal)) || IsCompilerGenerated(mi))
            {
                continue;
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

    /// <summary>
    /// Review finding (M3): a cursor was a bare offset with no idea what request produced it -- replaying
    /// a list_functions cursor into a differently-scoped list_functions call (different namespace/type),
    /// or into search_functions entirely, silently returned a wrong-but-plausible page instead of an
    /// error, since the offset alone is meaningless without knowing which sorted list it indexes into.
    /// Cursors now embed a short hash of the scope (namespace/type_name/query) that produced them;
    /// replaying one against a different scope is now a clear params error, not silent wrong data.
    ///
    /// Deliberately NOT string.GetHashCode() -- .NET randomizes that per-process by default (a DoS
    /// mitigation), so a cursor handed out by one broker connection could fail to validate against the
    /// exact same scope on a different run. SHA256 is stable across processes/runs, which is what a
    /// cursor's own contract (PRD §08: "opaque pagination cursor echoed back") requires.
    /// </summary>
    private static string BuildCursor(int offset, string scopeKey) => $"{offset}:{HashScope(scopeKey)}";

    private static string HashScope(string scopeKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(scopeKey));
        return Convert.ToHexString(hash)[..8];
    }

    /// <summary>Parses a list_functions/search_functions cursor ("offset:scopeHash") defensively -- an invalid/non-numeric offset, or one whose scope hash doesn't match the CURRENT request's scope, is a clear params error, not a crash or a silently-wrong page.</summary>
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
