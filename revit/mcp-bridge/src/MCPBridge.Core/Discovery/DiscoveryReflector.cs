using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MCPBridge.Core.Discovery;

/// <summary>One reflected member of one reflected type, ready to persist into <see cref="DiscoveryCache"/>'s members/members_fts tables.</summary>
public sealed class ReflectedMember
{
    /// <summary>"Type" / "Method" / "Property" / "Field" / "Constructor" / "Event".</summary>
    public required string Kind { get; init; }

    public required string Name { get; init; }

    /// <summary>Compact, human-readable C#-ish rendering (see <see cref="SignatureFormatter"/>).</summary>
    public required string Signature { get; init; }

    public string? Summary { get; init; }

    /// <summary>The XML doc-id (<see cref="XmlDocId.GetDocId"/>'s output) -- doubles as describe_function's member_id.</summary>
    public required string MemberId { get; init; }

    public string? Returns { get; init; }

    public required IReadOnlyList<ReflectedParameter> Parameters { get; init; }
}

public sealed class ReflectedParameter
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Description { get; init; }
}

/// <summary>One reflected type, ready to persist into <see cref="DiscoveryCache"/>'s types/members tables.</summary>
public sealed class ReflectedType
{
    public required string Namespace { get; init; }

    /// <summary>Fully-qualified, dotted name ('+' nesting separators flattened to '.').</summary>
    public required string FullName { get; init; }

    /// <summary>Short/unqualified type name (Type.Name -- may itself contain '+' for a nested type; callers strip further as needed).</summary>
    public required string Name { get; init; }

    public required string MemberId { get; init; }

    /// <summary>Whether the assembly's XML-doc sidecar has a "T:" entry for this type -- see this field's use in <see cref="DiscoveryService"/> for the browse-vs-lookup distinction it drives.</summary>
    public required bool Documented { get; init; }

    /// <summary>Fully-qualified base type name (dotted), if the base type is itself part of this or another reflected assembly's surface; used to walk the inheritance chain for "members including inherited" queries. Null for interfaces/System.Object-rooted types with no in-surface base.</summary>
    public string? BaseFullName { get; init; }

    public required IReadOnlyList<ReflectedMember> Members { get; init; }
}

/// <summary>
/// The pure reflection engine behind PRD §08's discovery commands: given one already-loaded <see
/// cref="Assembly"/>, produces every publicly-visible, C#-shaped type in it (see <see
/// cref="IsPubliclyVisible"/>) plus its declared members, joined against the assembly's XML-doc sidecar.
///
/// <para>
/// Extracted out of what used to be <see cref="DiscoveryService"/> itself (this feature's original,
/// fully-in-memory shape) so the same reflection/filtering/doc-join logic can back two different storage
/// strategies: <see cref="DiscoveryCache"/>'s persistent SQLite sync (production, PRD §08's cache addendum)
/// and any lightweight in-memory use a test wants without a database. Deliberately stateless and
/// side-effect-free beyond reading the assembly's own XML sidecar file -- no persistence, no caching,
/// callers own that.
/// </para>
/// </summary>
public static class DiscoveryReflector
{
    /// <summary>
    /// list_functions/search_functions truncate a summary to this length (PRD §08's response-size
    /// budget); describe_function does not -- it's inherently single-member-scoped, so the full text fits
    /// well under the token ceiling regardless. Reflection therefore stores the FULL summary; truncation is
    /// applied by <see cref="DiscoveryService"/> only when building the two paginated/scanning wire shapes,
    /// not here (this used to be truncated at reflection time, before summary text lived in a persistent
    /// cache shared by all three commands -- doing it here would have quietly started truncating
    /// describe_function's summary too, a behavior change nothing asked for).
    /// </summary>
    public const int MaxSummaryLength = 300;

    /// <summary>
    /// Bump whenever reflection's OUTPUT for an unchanged assembly changes -- a new signature rendering, a
    /// member kind newly included or excluded, a doc-join fix. <see cref="DiscoveryCache"/> persists
    /// reflected rows keyed by the assembly file's hash, and RevitAPI.dll's bytes do not change when the
    /// add-in is upgraded, so without this stamp an upgrade keeps serving whatever the previous reflector
    /// wrote (independent review of #186: the accessor rendering would never have reached an existing
    /// install's cache). The stamp is folded into the stored hash, so a mismatch re-reflects on first sync.
    /// History: 1 = every version before #186; 2 = #186's named-indexed-property rendering.
    /// </summary>
    public const string ReflectorVersion = "2";

    /// <summary>Reflects every publicly-visible type (and its declared members) out of one assembly.</summary>
    public static IReadOnlyList<ReflectedType> Reflect(Assembly assembly)
    {
        XmlDocIndex docIndex;
        try
        {
            docIndex = string.IsNullOrEmpty(assembly.Location)
                ? XmlDocIndex.Empty
                : XmlDocIndex.LoadFromFile(Path.ChangeExtension(assembly.Location, ".xml"));
        }
        catch
        {
            docIndex = XmlDocIndex.Empty;
        }

        Type[] assemblyTypes;
        try
        {
            assemblyTypes = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            assemblyTypes = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        var visibleTypes = new List<Type>();
        foreach (var t in assemblyTypes)
        {
            if (IsPubliclyVisible(t))
            {
                visibleTypes.Add(t);
            }
        }

        var results = new List<ReflectedType>();
        foreach (var t in visibleTypes)
        {
            ReflectedType reflected;
            try
            {
                reflected = ReflectType(t, docIndex);
            }
            catch
            {
                // Review finding (H2), carried over from the original DiscoveryService: reflecting a single
                // type can throw for a type with unresolvable references (plausible in a C++/CLI interop
                // assembly like RevitAPI.dll). A single pathological type must not fail the entire sync.
                continue;
            }

            results.Add(reflected);
        }

        return results;
    }

    private static ReflectedType ReflectType(Type t, XmlDocIndex docIndex)
    {
        var fullName = (t.FullName ?? t.Name).Replace('+', '.');
        var typeMemberId = XmlDocId.GetDocId(t);
        var documented = ReferenceEquals(docIndex, XmlDocIndex.Empty) || docIndex.TryGet(typeMemberId, out _);

        var baseFullName = t.BaseType is null ? null : (t.BaseType.FullName ?? t.BaseType.Name)?.Replace('+', '.');

        // Independent PR review finding: this must be its OWN try/catch, separate from the type-level fields
        // above. The original in-memory DiscoveryService caught a member-reflection failure here specifically
        // and kept the type (with an empty member list) rather than dropping it -- a type whose member
        // enumeration throws (a C++/CLI interop type with an unresolvable reference is the realistic case)
        // is still real and still worth being able to find via list_functions/describe_function, just with
        // nothing more specific to say about its members. Wrapping the WHOLE ReflectType call in one
        // try/catch (as an earlier version of this extraction did) silently regressed that: the caller's own
        // catch would drop the type entirely instead, making it vanish from the discovery surface rather
        // than degrade gracefully.
        List<ReflectedMember> members;
        try
        {
            members = ReflectMembers(t, docIndex);
        }
        catch
        {
            members = new List<ReflectedMember>();
        }

        return new ReflectedType
        {
            Namespace = t.Namespace ?? "",
            FullName = fullName,
            Name = t.Name,
            MemberId = typeMemberId,
            Documented = documented,
            BaseFullName = baseFullName,
            Members = members,
        };
    }

    /// <summary>
    /// Whether a reflected type belongs on the API surface at all. Uses <see cref="Type.IsVisible"/>, NOT
    /// <c>IsPublic || IsNestedPublic</c>: the latter is wrong for nested types (true for a public type nested
    /// inside an internal one, which is not externally visible at all). Also rejects types whose full name
    /// isn't shaped like a C# identifier path -- RevitAPI.dll is a C++/CLI assembly whose build emits
    /// native/ATL artifacts into public metadata with mangled names no C# API ever has.
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

    /// <summary>
    /// Deliberately reads attribute METADATA by name rather than calling
    /// <c>GetCustomAttribute(typeof(CompilerGeneratedAttribute))</c>. The typeof form needs to instantiate
    /// the attribute and compare Type identity against the running context, which throws outright under a
    /// <c>MetadataLoadContext</c> -- and a MetadataLoadContext is the only way to reflect over the real
    /// RevitAPI.dll off a live Revit process, since it is a native x64 mixed-mode assembly that a test host
    /// on this ARM64 dev VM cannot load for execution at all.
    ///
    /// <para>Not an exact equivalence, and worth stating precisely (independent PR review finding): the
    /// <c>GetCustomAttribute(MemberInfo, Type)</c> extension resolves to <c>Attribute.GetCustomAttribute</c>,
    /// which searches with <c>inherit: true</c>, whereas <c>GetCustomAttributesData()</c> is declared-only
    /// and has no inherit overload. <c>CompilerGeneratedAttribute</c> is <c>Inherited = true</c>, so an
    /// override of a compiler-generated base member would now be kept where it was previously filtered.
    /// Unreachable in practice for the assemblies this reflects over, but "unchanged" would be too strong
    /// a claim.</para>
    /// </summary>
    private static bool IsCompilerGenerated(MemberInfo m) =>
        m.GetCustomAttributesData().Any(a =>
            a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");

    private static List<ReflectedMember> ReflectMembers(Type type, XmlDocIndex docIndex)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var results = new List<ReflectedMember>();

        foreach (var ci in type.GetConstructors(flags))
        {
            results.Add(BuildMember(ci, "Constructor", docIndex));
        }

        foreach (var mi in type.GetMethods(flags))
        {
            // Review finding (L1), carried over: IsSpecialName is true for both property/event accessors
            // (get_/set_/add_/remove_) AND operator overloads (op_Equality, op_Implicit, etc.) -- only skip
            // the accessor shapes, not op_*, or every operator becomes permanently undiscoverable.
            if ((mi.IsSpecialName && !mi.Name.StartsWith("op_", StringComparison.Ordinal)) || IsCompilerGenerated(mi))
            {
                continue;
            }

            results.Add(BuildMember(mi, "Method", docIndex));
        }

        foreach (var pi in type.GetProperties(flags))
        {
            if (IsCompilerGenerated(pi))
            {
                continue;
            }

            results.Add(BuildMember(pi, "Property", docIndex));
        }

        foreach (var fi in type.GetFields(flags))
        {
            if (fi.IsSpecialName || IsCompilerGenerated(fi))
            {
                continue; // e.g. auto-property backing fields.
            }

            results.Add(BuildMember(fi, "Field", docIndex));
        }

        foreach (var ei in type.GetEvents(flags))
        {
            if (IsCompilerGenerated(ei))
            {
                continue;
            }

            results.Add(BuildMember(ei, "Event", docIndex));
        }

        return results;
    }

    private static ReflectedMember BuildMember(MemberInfo info, string kind, XmlDocIndex docIndex)
    {
        var declaringType = info.DeclaringType!;
        var memberId = XmlDocId.GetDocId(info);
        docIndex.TryGet(memberId, out var docEntry);

        var parameters = GetParameters(info)
            .Select(p => new ReflectedParameter
            {
                Name = p.Name ?? "",
                Type = SignatureFormatter.ParamTypeName(p.ParameterType),
                Description = docEntry?.Parameters.TryGetValue(p.Name ?? "", out var desc) == true ? desc : null,
            })
            .ToList();

        return new ReflectedMember
        {
            MemberId = memberId,
            Kind = kind,
            Name = info is ConstructorInfo ? declaringType.Name : info.Name,
            Signature = SignatureFormatter.BuildSignature(info),
            Summary = docEntry?.Summary,
            Returns = docEntry?.Returns,
            Parameters = parameters,
        };
    }

    private static ParameterInfo[] GetParameters(MemberInfo info) => info switch
    {
        MethodInfo mi => mi.GetParameters(),
        ConstructorInfo ci => ci.GetParameters(),
        PropertyInfo pi => pi.GetIndexParameters(),
        _ => Array.Empty<ParameterInfo>(),
    };

    /// <summary>Shared by <see cref="DiscoveryService"/>'s list_functions/search_functions paths -- see <see cref="MaxSummaryLength"/>'s own doc comment for why this lives here as a reusable helper rather than being applied at reflection time.</summary>
    public static string? Truncate(string? summary)
    {
        if (string.IsNullOrEmpty(summary) || summary.Length <= MaxSummaryLength)
        {
            return summary;
        }

        return summary[..MaxSummaryLength].TrimEnd() + "...";
    }
}
