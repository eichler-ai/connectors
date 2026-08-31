using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
/// <para><b>Markers are advisory, not authenticated.</b> They are ordinary JSON strings, so a script can
/// produce one: <c>return "&lt;Autodesk.Revit.DB.Level: no display form ...&gt;";</c> is byte-identical to
/// the real thing, and a returned string is passed through verbatim. That is deliberate rather than
/// overlooked -- a distinguishable shape (an object with a reserved member, say) would make every ordinary
/// result harder to read to defend against a case with no motive: nothing here is a security boundary, and
/// a script that wants to lie about its own return value can simply return a lie. The property that
/// matters is the one the issue asked for and this does provide: a value the connector could not render
/// never SILENTLY arrives looking like data.</para>
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

    /// <summary>
    /// Wall-clock ceiling for one Format call. See <see cref="Budget"/> for why a time limit is not
    /// redundant with the volume limits. Two seconds is deliberately generous -- a well-behaved graph
    /// finishes in microseconds, so anything approaching this is already pathological.
    /// </summary>
    internal static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(2);

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

        /// <summary>
        /// A wall-clock ceiling alongside the volume ceilings, because they bound different things
        /// (independent review). The node and character budgets bound how MUCH work this does; they say
        /// nothing about how LONG it takes. Where the old one-liner ran a single ToString(), this can run
        /// thousands of script-controlled property getters and MoveNext calls, on Revit's UI thread, after
        /// the run is already complete and past any max_duration_ms cancellation. A thousand
        /// slow-but-returning getters wedge Revit for as long as they take, and only a clock notices.
        ///
        /// <para>Honest about what this does NOT do: nothing here can preempt a SINGLE getter that blocks
        /// or spins forever -- .NET has no safe way to abort one, and pretending otherwise would be worse
        /// than saying so. It bounds the accumulation, which is the case bounded work makes reachable.</para>
        /// </summary>
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public bool OutOfTime => _clock.Elapsed > TimeLimit;

        public bool Exhausted => NodesLeft <= 0 || CharsLeft <= 0 || OutOfTime;

        public bool TakeNode() => NodesLeft-- > 0 && !OutOfTime;
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
            return Marker(
                budget.OutOfTime
                    ? "<truncated: formatting the return value exceeded the connector's " + TimeLimit.TotalSeconds + "s budget>"
                    : "<truncated: return value exceeded the connector's " + MaxNodes + "-value serialization budget>",
                budget);
        }

        switch (value)
        {
            case string s:
                return TakeString(s, budget);
            case char c:
                return TakeString(c.ToString(), budget);
            case bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                // Enums are deliberately NOT covered by this arm, and the CLR agrees: `value is int` is
                // false for a boxed enum even when its underlying type is int, so an enum reaches its own
                // arm below and renders as its NAME rather than its ordinal -- which is what an agent
                // wants back from a script that returns one.
                //
                // These pass through as their boxed selves so JsonSerializer emits real JSON numbers and
                // booleans rather than strings. At the root, Format renders them with the INVARIANT
                // culture; the old ToString() used the current one, so a machine with a comma decimal
                // separator used to return "12,5" here.
                return value;
            case Enum e:
                return TakeString(e.ToString(), budget);
            case DateTime or DateTimeOffset or TimeSpan or Guid:
                return TakeString(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture), budget);
        }

        if (depth >= MaxDepth)
        {
            return Marker("<max depth " + MaxDepth + " reached: " + value.GetType().Name + " not expanded>", budget);
        }

        // Reference cycles: an anonymous-type graph cannot contain one, but a script-defined class can
        // trivially (`a.Next = b; b.Next = a;`), and depth alone would only turn that into MaxDepth
        // levels of noise rather than naming the real shape.
        foreach (var ancestor in ancestors)
        {
            if (ReferenceEquals(ancestor, value))
            {
                return Marker("<circular reference to " + value.GetType().Name + ">", budget);
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
                    Put(result, "<truncated>", "more than " + MaxCollectionItems + " entries; only the first " + MaxCollectionItems + " are shown");
                    break;
                }

                // A key whose ToString() throws must not lose the whole dictionary; FormatScalar already
                // owns that guarantee, so keys go through it rather than calling ToString() here.
                var key = FormatScalar(entry.Key, budget);
                Put(result, key, Convert(entry.Value, depth + 1, ancestors, budget));
                emitted++;

                if (budget.Exhausted)
                {
                    Put(result, "<truncated>", "the connector's serialization budget ran out after " + emitted + " entries");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Put(result, "<enumeration failed>", ex.GetType().Name);
        }
        finally
        {
            ancestors.RemoveAt(ancestors.Count - 1);
        }

        return result;
    }

    /// <summary>
    /// Adds one member to an object being built, DISAMBIGUATING a name that is already there rather than
    /// overwriting it.
    ///
    /// <para>Plain assignment was silent data loss, and the shape that triggers it is idiomatic
    /// (independent review): <c>elements.ToDictionary(e =&gt; e, ...)</c> gives every key the SAME
    /// "no display form" text, so a 400-entry dictionary collapsed to one member with nothing saying so.
    /// A JSON object cannot hold duplicate names, so the honest options are to rename or to drop loudly;
    /// renaming keeps the values. Also covers a script key that happens to spell one of this class's own
    /// <c>&lt;truncated&gt;</c> markers, and a type whose <c>new</c>-shadowed property appears twice in
    /// GetProperties.</para>
    /// </summary>
    private static void Put(Dictionary<string, object?> result, string name, object? value)
    {
        if (result.TryAdd(name, value))
        {
            return;
        }

        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            if (result.TryAdd(name + " #" + suffix, value))
            {
                return;
            }
        }
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

                if (budget.Exhausted)
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
            // Member count is capped by the same number as a collection's element count. A type with
            // thousands of properties was otherwise bounded only by MaxNodes, which is the whole-graph
            // budget -- one wide object could spend all of it.
            var members = 0;

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0 || property.GetMethod is null || !property.GetMethod.IsPublic)
                {
                    continue;
                }

                if (members >= MaxCollectionItems)
                {
                    Put(result, "<truncated>", "more than " + MaxCollectionItems + " members; the rest are not shown");
                    break;
                }

                members++;

                // Member NAMES are charged too: they are as much of the emitted message as the values, and
                // a type from a script can have arbitrarily long ones.
                var name = Marker(property.Name, budget);

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
                    Put(result, name, Marker("<getter threw " + actual.GetType().Name + ">", budget));
                    continue;
                }

                Put(result, name, Convert(member, depth + 1, ancestors, budget));
                if (budget.Exhausted)
                {
                    break;
                }
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (members >= MaxCollectionItems)
                {
                    Put(result, "<truncated>", "more than " + MaxCollectionItems + " members; the rest are not shown");
                    break;
                }

                members++;
                var name = Marker(field.Name, budget);

                object? member;
                try
                {
                    member = field.GetValue(value);
                }
                catch (Exception ex)
                {
                    Put(result, name, Marker("<field read threw " + ex.GetType().Name + ">", budget));
                    continue;
                }

                Put(result, name, Convert(member, depth + 1, ancestors, budget));
                if (budget.Exhausted)
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
            return Marker("<" + type.FullName + ": ToString() threw " + ex.GetType().Name + ">", budget);
        }

        if (text is null)
        {
            return Marker("<" + type.FullName + ": ToString() returned null>", budget);
        }

        if (string.Equals(text, type.ToString(), StringComparison.Ordinal))
        {
            return Marker(
                "<" + type.FullName + ": no display form -- this type neither overrides ToString() nor is one the connector serializes. " +
                "Return a projection of the values you need (e.g. new { x.Name, x.Id }) instead.>",
                budget);
        }

        return TakeString(text, budget);
    }

    /// <summary>
    /// Emits one of this class's own <c>&lt;...&gt;</c> markers, charging it to the shared character
    /// budget but never truncating it -- a half-written explanation is worse than none.
    ///
    /// <para>Charging matters and was a real gap: markers used to bypass the budget entirely, so
    /// <c>return listOf500Elements;</c> -- 500 values with no display form, the exact shape a script hits
    /// when it forgets to project -- emitted 500 unbudgeted ~200-character markers and blew past the
    /// documented 64 KB by an order of magnitude. Charging them makes the collection loop's existing
    /// <c>CharsLeft &lt;= 0</c> break fire, so the marker path is bounded by the same number as the data
    /// path.</para>
    /// </summary>
    private static string Marker(string text, Budget budget)
    {
        budget.CharsLeft -= text.Length;
        return text;
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

        // Never cut between a surrogate pair (independent review). The limit is a UTF-16 code-unit index,
        // so a cut mid-pair leaves a lone high surrogate -- which is not valid UTF-16, which Utf8JsonWriter
        // REJECTS. That would throw out of Serialize, hit SafeFormatReturnValue's last-resort catch, and
        // lose the entire return value to one emoji in one long string. Any non-BMP character in a Revit
        // parameter reaches this.
        if (limit > 0 && char.IsHighSurrogate(text[limit - 1]))
        {
            limit--;
        }

        // Marker charges the whole thing, kept characters and suffix alike -- the suffix used to be free,
        // which mattered because once CharsLeft hits 0 every subsequent string emits one.
        return Marker(text.Substring(0, limit) + "<truncated: " + (text.Length - limit) + " more characters>", budget);
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
        // class or a record in the script text -- has no on-disk location. That is what this selects.
        //
        // Stated honestly rather than as a guarantee (independent review): this is BROADER than
        // "script-defined types and only those". Anything else in Revit's AppDomain that is dynamic or was
        // loaded from bytes -- a Reflection.Emit proxy, a third-party add-in's Assembly.Load(byte[])
        // assembly, a single-file-published assembly -- also has an empty Location and would be walked.
        // The narrower rule is to match the submission assembly's own identity, which RoslynScriptRunner
        // knows and this class does not; that plumbing is the real fix if one of those types ever shows up
        // here. Nothing shipped with the connector or with Revit matches today (both are ordinary on-disk
        // assemblies), so the exposure is a third-party add-in's in-memory type reaching a script's return
        // value, and the blast radius is bounded reflection over it, not a capability leak.
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
