using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCPBridge.Core.Protocol;

/// <summary>
/// Gives one enum member a custom JSON wire name, decoupling it from the C# identifier
/// (PRD §01/§06/§10: several enums need lowercase, hyphenated wire values that don't
/// match their PascalCase member names -- e.g. AddIn -&gt; "add-in", Completed -&gt; "success").
/// A hand-rolled equivalent of System.Text.Json's JsonStringEnumMemberNameAttribute,
/// which is .NET 9+ only; this project multi-targets net10.0-windows and net8.0-windows
/// (PRD §11), so the framework attribute isn't available on the net8.0-windows leg.
/// Written once here rather than duplicated per enum, and works identically on both
/// targets -- no #if/conditional-compilation divergence to keep in sync.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class WireEnumNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// Pair with <c>[JsonConverter(typeof(WireEnumNameConverter&lt;TEnum&gt;))]</c> on the enum
/// itself; reads each member's <see cref="WireEnumNameAttribute"/> via reflection once
/// (cached per closed generic type) rather than per (de)serialization call. A member with
/// no attribute falls back to its plain <c>ToString()</c> spelling.
/// </summary>
public sealed class WireEnumNameConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> ToWire = BuildToWire();
    private static readonly Dictionary<string, TEnum> FromWire =
        ToWire.ToDictionary(kv => kv.Value, kv => kv.Key);

    private static Dictionary<TEnum, string> BuildToWire()
    {
        var map = new Dictionary<TEnum, string>();
        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (TEnum)field.GetValue(null)!;
            var wireName = field.GetCustomAttribute<WireEnumNameAttribute>()?.Name ?? value.ToString();
            map[value] = wireName;
        }
        return map;
    }

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        if (raw is not null && FromWire.TryGetValue(raw, out var value))
        {
            return value;
        }
        throw new JsonException($"Unknown {typeof(TEnum).Name} wire value: \"{raw}\"");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToWire.TryGetValue(value, out var name) ? name : value.ToString());
}
