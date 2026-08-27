using System;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MCPBridge.Core.Discovery;

/// <summary>
/// Computes the standard .NET XML-doc-comment id string (the same format used as the `name` attribute of
/// each <c>&lt;member&gt;</c> node in RevitAPI.xml/RevitAPIUI.xml, and by every other .NET compiler-emitted
/// doc-comment sidecar) for a reflected <see cref="MemberInfo"/> -- this is what lets
/// <see cref="XmlDocIndex"/> join a live-reflected member back to its doc-comment text.
///
/// <para>
/// Covers the common cases this feature actually needs (PRD §08's public API surface): methods with
/// 0/1/2+ primitive/class/interface params, properties (including indexers), fields, events, constructors
/// (rendered as the synthetic name "#ctor"), and simple (closed, one level of) generic types used as a
/// parameter type -- e.g. <c>ICollection&lt;ElementId&gt;</c> becomes
/// <c>System.Collections.Generic.ICollection{Autodesk.Revit.DB.ElementId}</c>, per the ECMA-334 Annex
/// convention (curly braces instead of angle brackets so the id string stays valid inside XML, and the
/// arity suffix is dropped in this position -- only present on the *declaring* type name, not on a
/// parameter's constructed-generic name). Deliberately does not attempt pointer types, ref/out modifiers,
/// or nested generic arity beyond one level -- not part of Revit's public API surface (see this feature's
/// task brief).
/// </para>
/// </summary>
public static class XmlDocId
{
    public static string GetDocId(MemberInfo member) => member switch
    {
        Type t => "T:" + BuildDeclaringTypeName(t),
        ConstructorInfo ci => "M:" + BuildDeclaringTypeName(ci.DeclaringType!) + ".#ctor" + BuildParamsSuffix(ci.GetParameters()),
        MethodInfo mi => "M:" + BuildDeclaringTypeName(mi.DeclaringType!) + "." + mi.Name +
                          (mi.IsGenericMethod ? "``" + mi.GetGenericArguments().Length : "") +
                          BuildParamsSuffix(mi.GetParameters()),
        PropertyInfo pi => "P:" + BuildDeclaringTypeName(pi.DeclaringType!) + "." + pi.Name + BuildParamsSuffix(pi.GetIndexParameters()),
        FieldInfo fi => "F:" + BuildDeclaringTypeName(fi.DeclaringType!) + "." + fi.Name,
        EventInfo ei => "E:" + BuildDeclaringTypeName(ei.DeclaringType!) + "." + ei.Name,
        _ => throw new ArgumentException($"unsupported member kind: {member.GetType()}", nameof(member)),
    };

    /// <summary>
    /// Full dotted name for a type used as a declaring type (i.e. NOT as a constructed-generic parameter
    /// type -- see <see cref="BuildParamTypeName"/> for that case). Nested types (FullName's "+" separator)
    /// are flattened to "."; generic type definitions keep reflection's own backtick-arity suffix
    /// (e.g. "List`1"), matching the convention.
    /// </summary>
    private static string BuildDeclaringTypeName(Type t)
    {
        var full = t.FullName ?? (string.IsNullOrEmpty(t.Namespace) ? t.Name : t.Namespace + "." + t.Name);
        return full.Replace('+', '.');
    }

    private static string BuildParamsSuffix(ParameterInfo[] parameters)
    {
        if (parameters.Length == 0)
        {
            return "";
        }

        var sb = new StringBuilder("(");
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(BuildParamTypeName(parameters[i].ParameterType));
        }

        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Type name in parameter position: closed generic types render as "Base{Arg1,Arg2}" (arity suffix
    /// dropped, args in curly braces) rather than the declaring-type backtick form.
    /// </summary>
    private static string BuildParamTypeName(Type t)
    {
        // Review finding (M1): a byref parameter's real XML-doc-id convention appends "@" to the element
        // type's name (e.g. "System.Int32@" for `out int`/`ref int`) -- this used to just unwrap to the
        // element type with no suffix, silently breaking the doc join for every out/ref parameter (Revit's
        // API does have some) rather than throwing, since a missing join just looks like "no doc text",
        // not an error.
        if (t.IsByRef)
        {
            return BuildParamTypeName(t.GetElementType()!) + "@";
        }

        if (t.IsGenericParameter)
        {
            var prefix = t.DeclaringMethod is not null ? "``" : "`";
            return prefix + t.GenericParameterPosition;
        }

        if (t.IsArray)
        {
            return BuildParamTypeName(t.GetElementType()!) + "[]";
        }

        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var baseName = TypeNameFormatting.StripArity(BuildDeclaringTypeName(t.GetGenericTypeDefinition()));
            var args = string.Join(",", t.GetGenericArguments().Select(BuildParamTypeName));
            return baseName + "{" + args + "}";
        }

        return BuildDeclaringTypeName(t);
    }
}
