using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCPBridge.Core.Execution;

/// <summary>
/// Turns a script's return value into the display string an agent gets back in the wire result's
/// <c>return_value</c> field (issue #117).
///
/// <para><b>Why this exists.</b> The previous formatting was <c>value as string ?? value.ToString()</c>,
/// which is correct for a string or a number and actively misleading for everything else: the idiomatic
/// agent shape <c>...Select(l =&gt; new { l.Name, l.Elevation }).ToList()</c> came back as
/// <c>"System.Collections.Generic.List`1[&lt;&gt;f__AnonymousType0#1`2[System.String,System.Double]]"</c> --
/// <see cref="object.ToString"/>'s default, which is the type's name. Nothing distinguished that from real
/// data, so a quick inspection script read as an answer when it had produced none. Two rules follow, and
/// both are load-bearing: structural values (collections, dictionaries, anonymous types, script-defined
/// types) serialize to JSON, and any value that reaches the <see cref="FormatScalar"/> fallback with only
/// the DEFAULT ToString() to offer is reported as an explicit <c>&lt;...&gt;</c> marker naming the type
/// rather than being passed off as its value.</para>
///
/// <para><b>Why it is bounded, and why it does not reflect over everything.</b> This runs on Revit's UI
/// thread the moment a run completes (see RequestDispatcher.SafeFormatReturnValue for why formatting
/// happens there and not on the TCP thread), over an object graph an untrusted script chose. So: a node
/// budget and a character budget cap total work, <see cref="MaxDepth"/> caps nesting, a reference stack
/// catches cycles, and every property getter is individually try/caught -- a getter is arbitrary code and
/// must not turn a completed run into an unhandled UI-thread exception. Every one of those limits reports
/// itself in-band as a <c>&lt;...&gt;</c> marker inside otherwise-valid JSON, because a silent truncation
/// would recreate exactly the bug this class fixes.</para>
///
/// <para><b>Reflection is deliberately narrow</b> (<see cref="IsReflectableShape"/>): anonymous types,
/// types from an assembly with no on-disk location (i.e. the Roslyn script submission -- a class or record
/// the script itself declared), tuples, and KeyValuePair. NOT arbitrary types, and specifically not Revit
/// API types: walking a <c>Document</c>'s or <c>Element</c>'s public properties would fan out into a
/// huge live object graph and call dozens of Revit API accessors that can throw or be expensive, to
/// produce something no agent asked for. A returned <c>Element</c> therefore gets the honest
/// "no display form" marker naming its type, and a <c>List&lt;Element&gt;</c> gets a JSON array of those
/// markers -- which is still strictly more information than the single type name it produced before,
/// because the count and the element type are now visible.</para>
///
/// <para><b>A root-level scalar stays raw, unquoted</b> -- <c>return Document.Title;</c> yields
/// <c>MCPBridgeTest</c>, not <c>"MCPBridgeTest"</c>. That is both the pre-existing behaviour for the most
/// common script shape and the more readable one; JSON quoting only buys something once there is structure
/// to disambiguate.</para>
/// </summary>
internal static class ReturnValueFormatter
{
    /// <summary>Deepest nesting level serialized. Beyond it, a <c>&lt;max depth&gt;</c> marker.</summary>
    internal const int MaxDepth = 6;

    /// <summary>Most elements emitted from any one collection; the rest collapse into one marker element.</summary>
    internal const int MaxCollectionItems = 500;

    /// <summary>
    /// Total values (scalars, object members, collection elements) emitted across the whole graph. This is
    /// the bound that actually stops a pathological graph: MaxCollectionItems alone is per-collection, so
    /// nesting could still multiply out to MaxCollectionItems^MaxDepth.
    /// </summary>
    internal const int MaxNodes = 5_000;

    /// <summary>
    /// Total characters of string content emitted across the whole graph. Bounds the other axis MaxNodes
    /// does not: 5,000 nodes of unbounded strings is still unbounded.
    /// </summary>
    internal const int MaxCharacters = 64 * 1024;

    /// <summary>Longest single string value emitted, independent of how much of the shared budget is left.</summary>
    internal const int MaxStringLength = 8 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        // Relaxed escaping deliberately: this string is itself embedded in the outbound JSON envelope,
        // which escapes it again, so the default HTML-safe encoder's < for '<' would survive the
        // outer decode as the literal text "<" in every marker an agent reads. There is no injection
        // surface to protect here -- the envelope's own encoder is what makes the wire message well-formed.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Without this, a single NaN or Infinity anywhere in the graph makes Serialize THROW, and the
        // caller's last-resort catch then reports the whole return value as "formatting threw" -- every
        // other value in it lost to one bad number. Revit geometry produces both routinely (a degenerate
        // curve's parameter, a division by a zero-length vector), and JSON has no literal for either, so
        // they render as the strings "NaN"/"Infinity" instead. Losing the exact JSON number type for
        // those two cases is a much smaller loss than losing the result.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>
    /// Formats <paramref name="value"/> (never null -- the caller maps null to a null result) for the wire.
    /// Never throws: any escape from the conversion below is caught by the caller, which has its own
    /// last-resort marker.
    /// </summary>
    internal static string Format(object value)
    {
        // A returned string is passed through verbatim, before any budget applies. This is the one shape
        // that was never broken, and it is deliberately exempt from MaxStringLength: skill.md's own
        // file-exchange example is `return File.ReadAllText(...)`, and truncating that at 8 KB would trade
        // issue #117's bug for a worse one. Strings NESTED inside a structure are still bounded -- there
        // the cap protects a message that has many of them.
        if (value is string root)
        {
            return root;
        }

        var budget = new Budget();
        var converted = Convert(value, 0, new List<object>(), budget);

        // A root-level scalar renders raw, not as a JSON literal -- see the class doc.
        return converted switch
        {
            null => "",
            string s => s,
            bool b => b ? "true" : "false",
            _ when IsNumber(converted) => System.Convert.ToString(converted, CultureInfo.InvariantCulture) ?? "",
            _ => JsonSerializer.Serialize(converted, Json),
        };
    }

    /// <summary>
    /// The shared, mutable budget for one Format call. A class, not a struct, precisely because every
    /// recursive frame must see the same remaining allowance -- a copied struct would make each branch
    /// think it had the whole budget to itself, which is the bug this bounds against.
    /// </summary>
    private sealed class Budget
    {
        public int NodesLeft = MaxNodes;
        public int CharsLeft = MaxCharacters;

        public bool TakeNode() => NodesLeft-- > 0;
    }

    /// <summary>
    /// Converts one value into a tree of BCL primitives / strings / List&lt;object?&gt; /
    /// Dictionary&lt;string, object?&gt; that <see cref="JsonSerializer"/> can render without any converter
    /// of its own. Building the neutral tree first (rather than writing JSON as we walk) is what lets the
    /// bounds and the raw-scalar-root rule be expressed in one place.
    /// </summary>
    private static object? Convert(object? value, int depth, List<object> ancestors, Budget budget)
    {
        if (value is null)
        {
            return null;
        }

        if (!budget.TakeNode())
        {
            return "<truncated: return value exceeded the connector's " + MaxNodes + "-value serialization budget>";
        }

        switch (value)
        {
            case string s:
                return TakeString(s, budget);
            case char c:
                return TakeString(c.ToString(), budget);
            case bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                // Enums are NOT here: `value is int` is false for an enum-typed box, so an enum falls
                // through to the IFormattable arm below and renders as its NAME, which is what an agent
                // wants from `return wall.Location` style code far more often than the ordinal.
                return value;
            case Enum e:
                return TakeString(e.ToString(), budget);
            case DateTime or DateTimeOffset or TimeSpan or Guid:
                return TakeString(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture), budget);
        }

        if (depth >= MaxDepth)
        {
            return "<max depth " + MaxDepth + " reached: " + value.GetType().Name + " not expanded>";
        }

        // Reference cycles: an anonymous-type graph cannot contain one, but a script-defined class can
        // trivially (`a.Next = b; b.Next = a;`), and depth alone would only turn that into MaxDepth
        // levels of noise rather than naming the real shape.
        foreach (var ancestor in ancestors)
        {
            if (ReferenceEquals(ancestor, value))
            {
                return "<circular reference to " + value.GetType().Name + ">";
            }
        }

        switch (value)
        {
            case IDictionary dictionary:
                return ConvertDictionary(dictionary, depth, ancestors, budget);
            case IEnumerable enumerable:
                return ConvertEnumerable(enumerable, depth, ancestors, budget);
        }

        return IsReflectableShape(value.GetType())
            ? ConvertByReflection(value, depth, ancestors, budget)
            : FormatScalar(value, budget);
    }

    private static object ConvertDictionary(IDictionary dictionary, int depth, List<object> ancestors, Budget budget)
    {
        var result = new Dictionary<string, object?>();
        ancestors.Add(dictionary);
        try
        {
            var emitted = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (emitted >= MaxCollectionItems)
                {
                    result["<truncated>"] = "more than " + MaxCollectionItems + " entries; only the first " + MaxCollectionItems + " are shown";
                    break;
                }

                // A key whose ToString() throws must not lose the whole dictionary; FormatScalar already
                // owns that guarantee, so keys go through it rather than calling ToString() here.
                var key = FormatScalar(entry.Key, budget);
                result[key] = Convert(entry.Value, depth + 1, ancestors, budget);
                emitted++;

                if (budget.NodesLeft <= 0 || budget.CharsLeft <= 0)
                {
                    result["<truncated>"] = "the connector's serialization budget ran out after " + emitted + " entries";
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            result["<enumeration failed>"] = ex.GetType().Name;
        }
        finally
        {
            ancestors.RemoveAt(ancestors.Count - 1);
        }

        return result;
    }

    private static object ConvertEnumerable(IEnumerable enumerable, int depth, List<object> ancestors, Budget budget)
    {
        var items = new List<object?>();
        ancestors.Add(enumerable);
        try
        {
            var more = false;
            var budgetRanOut = false;

            // Enumeration STOPS at the limit rather than running on to count the total. Reporting
            // "500 shown of 4,812" would read better, but a script can return a lazily-generated
            // sequence that never ends, and counting it would spin Revit's UI thread forever -- a hang
            // where the old ToString() merely printed a type name. Bounded and vaguer beats unbounded.
            foreach (var item in enumerable)
            {
                if (items.Count >= MaxCollectionItems)
                {
                    more = true;
                    break;
                }

                items.Add(Convert(item, depth + 1, ancestors, budget));

                if (budget.NodesLeft <= 0 || budget.CharsLeft <= 0)
                {
                    budgetRanOut = true;
                    break;
                }
            }

            if (budgetRanOut)
            {
                items.Add("<truncated: the connector's serialization budget ran out after " + items.Count + " items>");
            }
            else if (more)
            {
                items.Add("<truncated: more than " + MaxCollectionItems + " items; the rest are not shown>");
            }
        }
        catch (Exception ex)
        {
            // Enumerating is arbitrary code too -- a lazy LINQ chain or a FilteredElementCollector runs
            // HERE, not where the script wrote it, so its exception surfaces at this call site.
            items.Add("<enumeration threw " + ex.GetType().Name + " after " + items.Count + " items>");
        }
        finally
        {
            ancestors.RemoveAt(ancestors.Count - 1);
        }

        return items;
    }

    private static object ConvertByReflection(object value, int depth, List<object> ancestors, Budget budget)
    {
        var type = value.GetType();
        var result = new Dictionary<string, object?>();
        ancestors.Add(value);
        try
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0 || property.GetMethod is null || !property.GetMethod.IsPublic)
                {
                    continue;
                }

                object? member;
                try
                {
                    member = property.GetValue(value);
                }
                catch (Exception ex)
                {
                    // Unwrap: reflection wraps whatever the getter threw in TargetInvocationException,
                    // whose own name says nothing about what actually went wrong.
                    var actual = (ex as TargetInvocationException)?.InnerException ?? ex;
                    result[property.Name] = "<getter threw " + actual.GetType().Name + ">";
                    continue;
                }

                result[property.Name] = Convert(member, depth + 1, ancestors, budget);
                if (budget.NodesLeft <= 0 || budget.CharsLeft <= 0)
                {
                    break;
                }
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object? member;
                try
                {
                    member = field.GetValue(value);
                }
                catch (Exception ex)
                {
                    result[field.Name] = "<field read threw " + ex.GetType().Name + ">";
                    continue;
                }

                result[field.Name] = Convert(member, depth + 1, ancestors, budget);
                if (budget.NodesLeft <= 0 || budget.CharsLeft <= 0)
                {
                    break;
                }
            }
        }
        finally
        {
            ancestors.RemoveAt(ancestors.Count - 1);
        }

        return result;
    }

    /// <summary>
    /// The last resort for a value this class will not reflect over. The <c>text == type.ToString()</c>
    /// check IS the fix for issue #117's core complaint: <see cref="object.ToString"/>'s default
    /// implementation returns exactly the type name, so that equality is a precise test for "this type
    /// offers no display form", and the caller gets told so instead of being handed a type name that looks
    /// like data.
    /// </summary>
    private static string FormatScalar(object? value, Budget budget)
    {
        if (value is null)
        {
            return "";
        }

        var type = value.GetType();
        string? text;
        try
        {
            text = value.ToString();
        }
        catch (Exception ex)
        {
            // No ex.Message: Message is virtual and script-definable, so interpolating it would hand the
            // guard against arbitrary script code straight back to arbitrary script code. GetType().Name
            // cannot run script code. (Carried over from the previous SafeFormatReturnValue -- a PR review
            // finding there, and equally true here.)
            return "<" + type.FullName + ": ToString() threw " + ex.GetType().Name + ">";
        }

        if (text is null)
        {
            return "<" + type.FullName + ": ToString() returned null>";
        }

        if (string.Equals(text, type.ToString(), StringComparison.Ordinal))
        {
            return "<" + type.FullName + ": no display form -- this type neither overrides ToString() nor is one the connector serializes. " +
                   "Return a projection of the values you need (e.g. new { x.Name, x.Id }) instead.>";
        }

        return TakeString(text, budget);
    }

    /// <summary>
    /// Emits a string against both string bounds. The per-string cap and the shared cap are separate on
    /// purpose: the first keeps one huge value from crowding out everything after it, the second keeps
    /// many merely-large values from summing to an unbounded message.
    /// </summary>
    private static string TakeString(string text, Budget budget)
    {
        var limit = Math.Min(MaxStringLength, Math.Max(budget.CharsLeft, 0));
        if (text.Length <= limit)
        {
            budget.CharsLeft -= text.Length;
            return text;
        }

        budget.CharsLeft -= limit;
        return text.Substring(0, limit) + "<truncated: " + (text.Length - limit) + " more characters>";
    }

    /// <summary>
    /// Which types this class will walk with reflection. Narrow by design -- see the class doc's
    /// "Reflection is deliberately narrow" paragraph for why an allowlist rather than a Revit denylist.
    /// </summary>
    private static bool IsReflectableShape(Type type)
    {
        if (IsAnonymousType(type))
        {
            return true;
        }

        // A Roslyn script submission is emitted to memory, so any type the script itself declared -- a
        // class or a record in the script text -- has no on-disk location. Nothing shipped with the
        // connector or with Revit matches this, so it selects script-defined types and only those.
        var assembly = type.Assembly;
        if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
        {
            return true;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(KeyValuePair<,>) || definition.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true
                || definition.FullName?.StartsWith("System.Tuple`", StringComparison.Ordinal) == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Anonymous types have no attribute that is theirs alone, so this matches the shape the C# compiler
    /// actually emits: compiler-generated, non-public, generic, and named &lt;&gt;f__AnonymousType*. The
    /// name check is the specific part; the rest keeps an ordinary type that happens to be named that way
    /// (which is not expressible in C# source) from qualifying.
    /// </summary>
    private static bool IsAnonymousType(Type type) =>
        type.IsGenericType
        && !type.IsPublic
        && type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false)
        && type.Name.Contains("AnonymousType", StringComparison.Ordinal);

    private static bool IsNumber(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;
}
