using System;
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
        return indexParams.Length > 0
            ? $"{TypeName(pi.PropertyType)} this[{ParamList(indexParams)}] {{ {accessors} }}"
            : $"{TypeName(pi.PropertyType)} {pi.Name} {{ {accessors} }}";
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
