using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MCPBridge.Core.Discovery;

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
        string.Join(", ", parameters.Select(p => $"{TypeName(p.ParameterType)} {p.Name}"));

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
