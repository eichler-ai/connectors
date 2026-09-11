using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Rhino.MCPBridge.Core.Discovery;

/// <summary>
/// Renders a reflected member as a compact, human-readable C#-ish signature string for MemberSignature.
/// Signature (PRD §08 example: "ICollection&lt;ElementId&gt; Delete(ElementId elementId)") -- short
/// alias names for common BCL types (int, not Int32), generics as angle brackets (not XmlDocId's
/// curly-brace form), and unqualified type names throughout (just "ElementId", never
/// "Autodesk.Revit.DB.ElementId" -- the surrounding MemberSignature.Namespace/DeclaringType fields already
/// carry full qualification).
/// </summary>
internal static class SignatureFormatter
{
    public static string BuildSignature(MemberInfo member) => member switch
    {
        Type t => TypeName(t),
        ConstructorInfo ci => $"{TypeName(ci.DeclaringType!)}({ParamList(ci.GetParameters())})",
        MethodInfo mi => $"{TypeName(mi.ReturnType)} {mi.Name}({ParamList(mi.GetParameters())})",
        PropertyInfo pi => BuildPropertySignature(pi),
        FieldInfo fi => $"{TypeName(fi.FieldType)} {fi.Name}",
        EventInfo ei => $"event {TypeName(ei.EventHandlerType ?? typeof(object))} {ei.Name}",
        _ => member.Name,
    };

    /// <summary>
    /// Renders how a member is CALLED from the Rhino CPython 3 host (PRD §09's "both call shapes" — this is
    /// the Python half; <see cref="BuildSignature"/> is the C# half). .NET interop differs from C# in ways
    /// an agent trips over, and this captures the load-bearing ones: a method's <c>out</c>/<c>ref</c>
    /// parameters come back in a return TUPLE instead of being passed by reference, a generic method takes
    /// explicit type arguments in square brackets (<c>Method[T](...)</c>), and a constructor is called
    /// WITHOUT <c>new</c>. Static members and constructors are qualified by the type (that is how both
    /// languages call them); an instance member is rendered receiver-less, exactly as the C# signature is,
    /// since the receiver is whatever object the caller holds. rhinoscriptsyntax functions get their own
    /// <c>rs.</c> form from <see cref="RhinoScriptIndexer"/>, not this method.
    /// </summary>
    public static string BuildPythonCall(MemberInfo member) => member switch
    {
        ConstructorInfo ci => $"{TypeName(ci.DeclaringType!)}({PyArgList(ci.GetParameters())})",
        MethodInfo mi => BuildMethodPythonCall(mi),
        PropertyInfo pi => BuildPropertyPythonCall(pi),
        FieldInfo fi => fi.IsStatic ? $"{TypeName(fi.DeclaringType!)}.{fi.Name}" : fi.Name,
        // An event is `obj.Name += handler` in both languages: nothing Python-specific to show.
        EventInfo ei => ei.Name,
        _ => member.Name,
    };

    private static string BuildMethodPythonCall(MethodInfo mi)
    {
        var name = mi.Name;
        if (mi.IsGenericMethodDefinition)
        {
            // CPython interop needs the type arguments written explicitly: Method[T1, T2](...).
            name += $"[{string.Join(", ", mi.GetGenericArguments().Select(a => a.Name))}]";
        }

        var call = mi.IsStatic
            ? $"{TypeName(mi.DeclaringType!)}.{name}({PyArgList(mi.GetParameters())})"
            : $"{name}({PyArgList(mi.GetParameters())})";

        // out/ref parameters are RETURNED (as a tuple with the return value), not passed by reference, in
        // the CPython host. `in` (readonly-ref) is passed like a normal argument and does not come back.
        var returned = mi.GetParameters().Where(ComesBackAsReturn).ToList();
        if (returned.Count == 0)
        {
            return call; // an ordinary call; a non-void return is assigned the usual way, nothing to show.
        }

        var lhs = new List<string>();
        if (mi.ReturnType != typeof(void))
        {
            lhs.Add("result");
        }

        lhs.AddRange(returned.Select(p => p.Name ?? "value"));
        return $"{string.Join(", ", lhs)} = {call}";
    }

    private static string BuildPropertyPythonCall(PropertyInfo pi)
    {
        var isStatic = (pi.GetMethod ?? pi.SetMethod)?.IsStatic == true;
        var indexParams = pi.GetIndexParameters();
        if (indexParams.Length == 0)
        {
            // A plain property is attribute access in both languages.
            return isStatic ? $"{TypeName(pi.DeclaringType!)}.{pi.Name}" : pi.Name;
        }

        if (IsDefaultMember(pi))
        {
            return $"[{PyArgList(indexParams)}]"; // the indexer: obj[args]
        }

        // A NAMED indexed property has no attribute/indexer syntax; the get_/set_ accessor methods are the
        // only spelling, in Python as in C# (issue #186, mirrored in BuildPropertySignature).
        return pi.CanRead
            ? $"get_{pi.Name}({PyArgList(indexParams)})"
            : $"set_{pi.Name}({PyArgList(indexParams)}, value)";
    }

    /// <summary>An <c>out</c> parameter, or a <c>ref</c> parameter (by-ref but not <c>in</c>), comes back in
    /// the CPython return tuple; a plain value or <c>in</c> parameter does not.</summary>
    private static bool ComesBackAsReturn(ParameterInfo p) => p.IsOut || (p.ParameterType.IsByRef && !p.IsIn);

    /// <summary>The Python argument list for a call: parameter NAMES only (Python is untyped at the call
    /// site), and <c>out</c> parameters dropped since the host supplies them via the return tuple. A
    /// <c>ref</c>/<c>in</c> parameter is still passed in.</summary>
    private static string PyArgList(ParameterInfo[] parameters) =>
        string.Join(", ", parameters.Where(p => !p.IsOut).Select(p => p.Name ?? "_"));

    private static string BuildPropertySignature(PropertyInfo pi)
    {
        var indexParams = pi.GetIndexParameters();
        var accessors = (pi.CanRead ? "get;" : "") + (pi.CanWrite ? "set;" : "");
        if (indexParams.Length == 0)
        {
            return $"{TypeName(pi.PropertyType)} {pi.Name} {{ {accessors} }}";
        }

        if (IsDefaultMember(pi))
        {
            return $"{TypeName(pi.PropertyType)} this[{ParamList(indexParams)}] {{ {accessors} }}";
        }

        // A NAMED indexed property (issue #186). C# has no syntax for one: `obj[...]` binds only to the
        // declaring type's DefaultMember, so rendering this as `this[...]` advertises a form that does not
        // compile, while the accessor methods -- the only C# spelling -- are the very `IsSpecialName`
        // methods DiscoveryReflector skips. RevitAPI.dll (C++/CLI) has 95 of these per version out of 104
        // indexed properties: 55 with their own names (Element.Parameter, Element.Geometry,
        // Element.BoundingBox, FamilyInstance.Room, FootPrintRoof.SlopeAngle, ...) and 40 called `Item`
        // that still carry no DefaultMemberAttribute (ModelCurveArray, PhaseArray, ParameterMap, ...), which
        // is why Revit C# code has always written `curves.get_Item(i)`. Only the 9 C++/CLI `default`
        // properties are true indexers. So this is the shape an agent actually has to type.
        var parts = new List<string>(2);
        if (pi.CanRead)
        {
            parts.Add($"{TypeName(pi.PropertyType)} get_{pi.Name}({ParamList(indexParams)})");
        }

        if (pi.CanWrite)
        {
            var setterParams = ParamList(indexParams);
            parts.Add($"void set_{pi.Name}({setterParams}, {TypeName(pi.PropertyType)} value)");
        }

        return string.Join("; ", parts);
    }

    /// <summary>
    /// True when this indexed property is the one C#'s <c>obj[...]</c> syntax binds to: its name matches the
    /// declaring type's <c>DefaultMemberAttribute</c>. Read as attribute METADATA by name, for the same
    /// MetadataLoadContext reason <c>DiscoveryReflector.IsCompilerGenerated</c> documents. A C# indexer
    /// always carries the attribute (named "Item", or whatever <c>[IndexerName]</c> chose); a C++/CLI
    /// <c>default</c> indexed property does too. A named indexed property never does.
    /// </summary>
    internal static bool IsDefaultMember(PropertyInfo pi)
    {
        // Walk the base chain: DefaultMemberAttribute is declared on the type that introduced the indexer,
        // and an override in a derived type need not re-declare it -- C# still binds `obj[...]` there.
        for (var type = pi.DeclaringType; type is not null; type = type.BaseType)
        {
            foreach (var attribute in type.GetCustomAttributesData())
            {
                if (attribute.AttributeType.FullName != "System.Reflection.DefaultMemberAttribute")
                {
                    continue;
                }

                if (attribute.ConstructorArguments.Count == 1 && attribute.ConstructorArguments[0].Value is string name)
                {
                    return string.Equals(name, pi.Name, StringComparison.Ordinal);
                }
            }
        }

        return false;
    }

    /// <summary>Public, describe_function-facing rendering of a single parameter's type (the "type" field of describe_function's parameters[] entries) -- same short/unqualified vocabulary as the rest of this class.</summary>
    public static string ParamTypeName(Type t) => TypeName(t);

    private static string ParamList(ParameterInfo[] parameters) =>
        string.Join(", ", parameters.Select(p => $"{DirectionKeyword(p)}{TypeName(p.ParameterType)} {p.Name}"));

    /// <summary>The C# by-reference keyword a parameter needs so the rendered signature actually compiles:
    /// <c>out</c>, <c>in</c> (readonly ref), or <c>ref</c>. Empty for an ordinary by-value parameter.</summary>
    private static string DirectionKeyword(ParameterInfo p)
    {
        if (p.IsOut)
        {
            return "out ";
        }

        if (p.ParameterType.IsByRef)
        {
            return p.IsIn ? "in " : "ref ";
        }

        return "";
    }

    private static string TypeName(Type t)
    {
        if (t.IsByRef)
        {
            return TypeName(t.GetElementType()!);
        }

        if (t.IsArray)
        {
            return TypeName(t.GetElementType()!) + "[]";
        }

        if (t.IsGenericParameter)
        {
            return t.Name;
        }

        if (t.IsGenericType)
        {
            var baseName = TypeNameFormatting.StripArity(t.Name);
            var args = string.Join(", ", t.GetGenericArguments().Select(TypeName));
            return $"{baseName}<{args}>";
        }

        return TypeNameFormatting.TryGetAlias(t);
    }
}
