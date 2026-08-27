using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCPBridge.Core.Protocol;

/// <summary>
/// Gives one enum member a custom JSON wire name, decoupling it from the C# identifier
/// (PRD §01/§06/§10: several enums need lowercase, hyphenated wire values that don't
/// match their PascalCase member names -- e.g. AddIn -&gt; "add-in", Completed -&gt; "success").
/// A hand-rolled stand-in for System.Text.Json's JsonStringEnumMemberNameAttribute,
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
/// no attribute falls back to its declared field name.
///
/// <para>
/// DELIBERATE DIVERGENCES from <c>JsonStringEnumConverter</c> + the framework's
/// <c>JsonStringEnumMemberNameAttribute</c>, so nobody later reads them as bugs. Both are
/// hardening, and both match what the Go broker actually puts on the wire:
/// </para>
/// <list type="bullet">
/// <item><description>Reads are case-SENSITIVE. The framework converter matched
/// case-insensitively; the wire values here are a fixed lowercase vocabulary shared with
/// the Go side, so a differently-cased value is a genuine protocol mismatch worth
/// surfacing rather than quietly accepting.</description></item>
/// <item><description>Integer values are NOT accepted. <c>JsonStringEnumConverter</c>
/// defaults to <c>allowIntegerValues: true</c>, so it would read <c>2</c> as a valid
/// member; the Go side never sends one, and silently accepting an ordinal would couple the
/// wire format to C# declaration order.</description></item>
/// <item><description><c>[Flags]</c> enums are NOT supported (the framework converter
/// round-trips comma-separated composites). Enforced at type-initialization time below
/// rather than left to produce a confusing half-working round trip.</description></item>
/// <item><description>ALIASED members (two names sharing one underlying value) are NOT
/// supported. Same posture as the <c>[Flags]</c> rule above but a DIFFERENT reason: flags
/// are rejected because the framework round-trips comma-separated composites, whereas
/// aliases are rejected because the value-keyed map physically cannot hold two wire names
/// for one value.</description></item>
/// <item><description>These enums cannot be used as DICTIONARY KEYS.
/// <c>JsonStringEnumConverter</c> overrides <c>ReadAsPropertyName</c>/<c>WriteAsPropertyName</c>;
/// this converter doesn't, so the base implementations throw <c>NotSupportedException</c>.
/// Latent -- nothing keys a dictionary on these today -- but listed here because that is
/// exactly what this block is for.</description></item>
/// </list>
/// </summary>
public sealed class WireEnumNameConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> ToWire = BuildToWire();
    private static readonly Dictionary<string, TEnum> FromWire = BuildFromWire(ToWire);

    private static Dictionary<TEnum, string> BuildToWire()
    {
        if (typeof(TEnum).IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            throw new NotSupportedException(
                $"{typeof(TEnum).FullName} is marked [Flags], which {nameof(WireEnumNameConverter<TEnum>)} does not support: " +
                "it would write a composite value as a single comma-separated string and then fail to read it back. " +
                "Give the enum a non-flags wire representation, or write a dedicated converter for it.");
        }

        var map = new Dictionary<TEnum, string>();
        var declaredBy = new Dictionary<TEnum, string>();

        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (TEnum)field.GetValue(null)!;

            // field.Name rather than value.ToString(). MOOT in practice: the alias guard below
            // rejects every enum in which the two could differ, so past that point they are always
            // equal. Kept deliberately so nobody "simplifies" it back to ToString() and quietly
            // reintroduces the dependency on which name the runtime considers canonical, should the
            // guard ever be relaxed.
            var wireName = field.GetCustomAttribute<WireEnumNameAttribute>()?.Name ?? field.Name;

            // ALIASES ARE REJECTED, and this guard is the actual fix -- using field.Name above is
            // not, on its own, sufficient. This map is keyed on the enum VALUE, so two members
            // sharing one underlying value collapse into a single slot no matter which name each
            // contributes: the last field declared simply wins, silently discarding the other's
            // wire name (including an explicit [WireEnumName], which would then also be unreadable,
            // since FromWire is derived from this map). There is no correct arbitrary winner, so
            // don't pick one. Same posture as the [Flags] and duplicate-wire-name guards.
            if (declaredBy.TryGetValue(value, out var firstField))
            {
                throw new InvalidOperationException(
                    $"{typeof(TEnum).FullName} declares {firstField} and {field.Name} with the same underlying value, " +
                    $"so they cannot have distinct wire names (\"{map[value]}\" and \"{wireName}\"). " +
                    "Aliased members are not supported: give each wire value exactly one member.");
            }

            declaredBy[value] = field.Name;
            map[value] = wireName;
        }

        return map;
    }

    // Built with an explicit loop rather than ToDictionary so a duplicated wire name reports
    // WHICH members collided. ToDictionary throws a bare "An item with the same key has
    // already been added" ArgumentException naming neither, and because this runs in static
    // initialization that surfaces as a TypeInitializationException which is then cached by
    // the CLR -- so every later use of the converter keeps failing with the same opaque
    // message for the rest of the process lifetime. A one-character typo in a
    // [WireEnumName] would become an unexplained protocol outage, which is exactly what
    // PRD §01 ("message names the concrete identifiers and the actual underlying
    // condition") exists to prevent.
    private static Dictionary<string, TEnum> BuildFromWire(Dictionary<TEnum, string> toWire)
    {
        var map = new Dictionary<string, TEnum>(StringComparer.Ordinal);
        foreach (var pair in toWire)
        {
            if (map.TryGetValue(pair.Value, out var existing))
            {
                throw new InvalidOperationException(
                    $"{typeof(TEnum).FullName} maps more than one member to the wire name \"{pair.Value}\": " +
                    $"{existing} and {pair.Key}. Wire names must be unique, or deserialization would be ambiguous.");
            }

            map[pair.Value] = pair.Key;
        }

        return map;
    }

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Guard the token kind explicitly: Utf8JsonReader.GetString() throws
        // InvalidOperationException (NOT JsonException) for a number/bool/null token, and an
        // InvalidOperationException escapes every `catch (JsonException)` a caller has
        // written. This is the decoder for the shared §01 diagnostic-record shape, whose own
        // contract promises a malformed wire payload still deserializes into an object
        // rather than throwing mid-parse -- so it has to fail as a JsonException.
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Expected a JSON string for {typeof(TEnum).Name} but found {reader.TokenType}. " +
                "Wire values are the lowercase names listed on the enum; integer/ordinal values are not accepted.");
        }

        // Non-null by construction: the token is known to be a String here.
        var raw = reader.GetString()!;
        if (FromWire.TryGetValue(raw, out var value))
        {
            return value;
        }

        throw new JsonException($"Unknown {typeof(TEnum).Name} wire value: \"{raw}\"");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        // No ToString() fallback. A value not in the map is an undefined ordinal cast into
        // the enum, and ToString() would emit it as a bare numeric string ("7") that this
        // converter's own Read then rejects -- a silently asymmetric round trip. Failing
        // loudly here matches §01's observability-over-silence posture.
        if (!ToWire.TryGetValue(value, out var name))
        {
            throw new JsonException(
                $"Cannot serialize {typeof(TEnum).FullName} value '{value}': it is not a declared member, " +
                "so it has no wire name. This usually means an out-of-range integer was cast into the enum.");
        }

        writer.WriteStringValue(name);
    }
}
