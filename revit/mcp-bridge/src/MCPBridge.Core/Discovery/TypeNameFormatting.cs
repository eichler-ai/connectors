using System;
using System.Collections.Generic;
using System.Linq;

namespace MCPBridge.Core.Discovery;

/// <summary>
/// Shared helpers for rendering a reflected <see cref="Type"/> as text, in the two different vocabularies
/// this feature needs: <see cref="XmlDocId"/>'s doc-comment-id format (full CLR names, generics as
/// backtick-arity or curly-brace-args) and <see cref="SignatureFormatter"/>'s human-readable, C#-ish
/// rendering (alias names, unqualified, angle-bracket generics) -- see MemberSignature.Signature's own
/// doc comment for why the two must differ.
/// </summary>
internal static class TypeNameFormatting
{
    /// <summary>Strips a generic type's trailing `N arity suffix (e.g. "List`1" -&gt; "List"), if present.</summary>
    public static string StripArity(string name)
    {
        var idx = name.LastIndexOf('`');
        return idx < 0 ? name : name[..idx];
    }

    /// <summary>C# alias names for the common BCL types this feature is expected to render read­ably (PRD §08 signature example: "int" not "Int32").</summary>
    private static readonly Dictionary<Type, string> Aliases = new()
    {
        [typeof(void)] = "void",
        [typeof(object)] = "object",
        [typeof(string)] = "string",
        [typeof(bool)] = "bool",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(char)] = "char",
        [typeof(decimal)] = "decimal",
        [typeof(double)] = "double",
        [typeof(float)] = "float",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
    };

    public static string TryGetAlias(Type type) => Aliases.TryGetValue(type, out var alias) ? alias : type.Name;
}
