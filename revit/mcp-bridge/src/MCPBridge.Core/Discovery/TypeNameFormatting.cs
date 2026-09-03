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

    /// <summary>
    /// C# alias names for the common BCL types this feature is expected to render readably (PRD §08 signature
    /// example: "int" not "Int32"). Keyed by full name rather than <see cref="Type"/> identity so the same
    /// rendering holds for a type reflected through a <c>MetadataLoadContext</c> (the only way the tests can
    /// reach the real RevitAPI.dll), whose <c>System.Double</c> is a different <see cref="Type"/> object from the
    /// runtime's and used to fall through to "Double" -- so RealRevitApiTests could never assert the signature
    /// an agent actually sees.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["System.Void"] = "void",
        ["System.Object"] = "object",
        ["System.String"] = "string",
        ["System.Boolean"] = "bool",
        ["System.Byte"] = "byte",
        ["System.SByte"] = "sbyte",
        ["System.Char"] = "char",
        ["System.Decimal"] = "decimal",
        ["System.Double"] = "double",
        ["System.Single"] = "float",
        ["System.Int32"] = "int",
        ["System.UInt32"] = "uint",
        ["System.Int64"] = "long",
        ["System.UInt64"] = "ulong",
        ["System.Int16"] = "short",
        ["System.UInt16"] = "ushort",
    };

    public static string TryGetAlias(Type type) =>
        type.FullName is { } fullName && Aliases.TryGetValue(fullName, out var alias) ? alias : type.Name;
}
