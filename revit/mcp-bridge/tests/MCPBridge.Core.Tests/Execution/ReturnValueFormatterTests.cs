using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Tests.Fakes;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

/// <summary>
/// Issue #117. The bug these pin is not "the output was ugly" -- it is that a caller could not tell data
/// from a type name, so a script that produced nothing read as a script that produced an answer. Two
/// halves, and both matter: structural values must serialize, and a value that cannot be serialized must
/// SAY so rather than falling back to a type name that looks like a value.
/// </summary>
public class ReturnValueFormatterTests
{
    // ------------------------------------------------------------------------------------------
    // The reported reproductions
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The exact shape from the issue: `...Select(l => new { l.Name, l.Elevation }).ToList()`, which came
    /// back as "System.Collections.Generic.List`1[&lt;&gt;f__AnonymousType0#1`2[System.String,System.Double]]".
    /// </summary>
    [Fact]
    public void ListOfAnonymousTypes_SerializesTheData_NotTheCollectionsTypeName()
    {
        var value = new[] { new { Name = "Level 1", Elevation = 0.0 }, new { Name = "Level 2", Elevation = 12.5 } }.ToList();

        var formatted = ReturnValueFormatter.Format(value);

        Assert.StartsWith("[{", formatted);
        Assert.Contains("\"Name\":\"Level 1\"", formatted);
        Assert.Contains("\"Elevation\":0", formatted);
        Assert.Contains("\"Name\":\"Level 2\"", formatted);
        Assert.Contains("\"Elevation\":12.5", formatted);
        Assert.DoesNotContain("f__AnonymousType", formatted);
    }

    [Fact]
    public void SingleAnonymousType_SerializesToJson()
    {
        var formatted = ReturnValueFormatter.Format(new { name = "Level 1", elevation = 0.0 });

        Assert.Contains("\"name\":\"Level 1\"", formatted);
        Assert.Contains("\"elevation\":0", formatted);
        Assert.StartsWith("{", formatted);
    }

    // ------------------------------------------------------------------------------------------
    // Scalars: the shapes that were never broken, and must stay unchanged
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void String_IsReturnedVerbatim_Unquoted()
    {
        Assert.Equal("MCPBridgeTest", ReturnValueFormatter.Format("MCPBridgeTest"));
    }

    /// <summary>
    /// skill.md's file-exchange example is `return File.ReadAllText(...)`, so a returned string is exempt
    /// from the character budget by construction -- fixing #117 must not introduce a silent truncation
    /// bug in its place.
    /// </summary>
    [Fact]
    public void String_LongerThanTheCharacterBudget_IsStillReturnedWhole()
    {
        var huge = new string('x', ReturnValueFormatter.MaxCharacters * 2);

        var formatted = ReturnValueFormatter.Format(huge);

        Assert.Equal(huge, formatted);
    }

    [Theory]
    [InlineData(42, "42")]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    [InlineData(12.5, "12.5")]
    public void RootScalar_RendersRawWithoutJsonQuoting(object value, string expected)
    {
        Assert.Equal(expected, ReturnValueFormatter.Format(value));
    }

    [Fact]
    public void Enum_RendersItsNameNotItsOrdinal()
    {
        Assert.Equal("Tuesday", ReturnValueFormatter.Format(DayOfWeek.Tuesday));
    }

    // ------------------------------------------------------------------------------------------
    // Structures
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void ListOfStrings_SerializesAsAJsonArray()
    {
        Assert.Equal("[\"a\",\"b\"]", ReturnValueFormatter.Format(new List<string> { "a", "b" }));
    }

    [Fact]
    public void Dictionary_SerializesAsAJsonObject()
    {
        var value = new Dictionary<string, int> { ["walls"] = 3 };

        Assert.Equal("{\"walls\":3}", ReturnValueFormatter.Format(value));
    }

    /// <summary>
    /// `elements.ToDictionary(e => e, ...)` is idiomatic, and every one of those keys formats to the same
    /// "no display form" text. Plain assignment collapsed the whole dictionary to one member with nothing
    /// saying so -- silent misrepresentation, which is the bug class this class exists to end.
    /// </summary>
    [Fact]
    public void DictionaryKeysThatFormatIdentically_AreDisambiguated_NotDropped()
    {
        var value = new Dictionary<object, int> { [new OpaqueThing()] = 1, [new OpaqueThing()] = 2 };

        var formatted = ReturnValueFormatter.Format(value);

        Assert.Contains(":1", formatted);
        Assert.Contains(":2", formatted);
        Assert.Contains("#2", formatted);
    }

    [Fact]
    public void DictionaryLongerThanMaxCollectionItems_IsTruncatedAndSaysSo()
    {
        var value = new Dictionary<int, int>();
        for (var i = 0; i < ReturnValueFormatter.MaxCollectionItems + 10; i++)
        {
            value[i] = i;
        }

        var formatted = ReturnValueFormatter.Format(value);

        Assert.Contains("more than " + ReturnValueFormatter.MaxCollectionItems + " entries", formatted);
    }

    [Fact]
    public void DictionaryOfLargeStrings_StopsAtTheSharedCharacterBudget()
    {
        var value = new Dictionary<int, string>();
        for (var i = 0; i < 100; i++)
        {
            value[i] = new string('q', 4096);
        }

        var formatted = ReturnValueFormatter.Format(value);

        Assert.Contains("serialization budget ran out", formatted);
        Assert.True(formatted.Length < CharacterCeiling, "got " + formatted.Length + " characters");
    }

    [Fact]
    public void KeyValuePairAndTuple_SerializeAsObjects()
    {
        Assert.Contains("\"Key\":\"a\"", ReturnValueFormatter.Format(new KeyValuePair<string, int>("a", 1)));
        Assert.Contains("\"Item1\":\"a\"", ReturnValueFormatter.Format(("a", 1)));
    }

    /// <summary>
    /// A type the SCRIPT declared, produced by really compiling one -- not a fixture class in this
    /// assembly, which would prove nothing here. IsReflectableShape selects script-defined types by their
    /// assembly having no on-disk location (a Roslyn submission is emitted to memory), and that is a claim
    /// about Roslyn's emit behaviour, not about this code: a fixture type in this test assembly HAS a
    /// location and is correctly not reflected. So the only way to test the rule is to run a script.
    /// </summary>
    [Fact]
    public void ScriptDefinedType_SerializesItsPublicPropertiesAndFields()
    {
        var value = RunScriptReturning(@"
            class Projection { public string Name { get; set; } public int Count; }
            return new Projection { Name = ""L1"", Count = 2 };");

        var formatted = ReturnValueFormatter.Format(value);

        Assert.Contains("\"Name\":\"L1\"", formatted);
        Assert.Contains("\"Count\":2", formatted);
    }

    /// <summary>
    /// The sibling half of the rule above, and the one that keeps a returned Revit Element from being
    /// walked: a type from an ordinary on-disk assembly is NOT reflected, even though its properties are
    /// perfectly readable. If this ever starts serializing, the guard that keeps a Document's or an
    /// Element's public surface from being enumerated on the UI thread is gone.
    /// </summary>
    [Fact]
    public void TypeFromAnOnDiskAssembly_IsNotWalkedByReflection()
    {
        var formatted = ReturnValueFormatter.Format(new Projection { Name = "L1", Count = 2 });

        Assert.DoesNotContain("\"Name\"", formatted);
        Assert.Contains("no display form", formatted);
    }

    /// <summary>
    /// JSON has no literal for either, and System.Text.Json's default is to THROW on them -- which would
    /// lose every other value in the graph to one degenerate number. Revit geometry produces both
    /// routinely, so this is a live shape rather than a theoretical one.
    /// </summary>
    [Fact]
    public void NaNAndInfinity_RenderAsNamedLiterals_RatherThanFailingTheWholeValue()
    {
        var formatted = ReturnValueFormatter.Format(new { ok = 1.5, bad = double.NaN, worse = double.PositiveInfinity });

        Assert.Contains("\"ok\":1.5", formatted);
        Assert.Contains("NaN", formatted);
        Assert.Contains("Infinity", formatted);
    }

    [Fact]
    public void NullElementsInsideACollection_SerializeAsJsonNull()
    {
        Assert.Equal("[\"a\",null]", ReturnValueFormatter.Format(new List<string?> { "a", null }));
    }

    // ------------------------------------------------------------------------------------------
    // The honest fallback -- the other half of the issue
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// A type with no ToString() override stands in for a Revit API object (which a tier-1 assembly cannot
    /// reference -- RevitAPI.dll is mixed-mode and unloadable here, see the skill's testing strategy).
    /// The behaviour under test is exactly what a returned Element hits: not serialized, no display form,
    /// so say so and name the type instead of emitting the type name bare.
    /// </summary>
    [Fact]
    public void TypeWithNoDisplayForm_SaysSoAndNamesTheType()
    {
        var formatted = ReturnValueFormatter.Format(new OpaqueThing());

        Assert.Contains("no display form", formatted);
        Assert.Contains(typeof(OpaqueThing).FullName!, formatted);
        Assert.NotEqual(typeof(OpaqueThing).ToString(), formatted);
    }

    [Fact]
    public void CollectionOfValuesWithNoDisplayForm_MarksEachOne_SoTheCountAndTypeStayVisible()
    {
        var formatted = ReturnValueFormatter.Format(new List<OpaqueThing> { new(), new() });

        Assert.StartsWith("[\"<", formatted);
        Assert.Equal(2, formatted.Split("no display form").Length - 1);
    }

    [Fact]
    public void TypeWithAMeaningfulToString_UsesIt()
    {
        Assert.Equal("I am useful", ReturnValueFormatter.Format(new UsefulToString()));
    }

    [Fact]
    public void ThrowingToString_IsReportedWithoutTheExceptionMessage()
    {
        var formatted = ReturnValueFormatter.Format(new ThrowingToString());

        Assert.Contains("ToString() threw InvalidOperationException", formatted);
        // The message is deliberately never interpolated: it is virtual and script-definable, so echoing
        // it would hand the guard against arbitrary script code back to arbitrary script code.
        Assert.DoesNotContain("secret", formatted);
    }

    /// <summary>
    /// A getter is arbitrary script code. Script-declared again, because only a script-defined type is
    /// reflected at all -- this is the exact shape where a property throwing could otherwise turn a
    /// COMPLETED run into an unhandled exception on Revit's UI thread.
    /// </summary>
    [Fact]
    public void ThrowingPropertyGetter_MarksThatMemberAndKeepsTheRest()
    {
        var value = RunScriptReturning(@"
            class PartlyThrowing
            {
                public string Fine => ""ok"";
                public string Broken => throw new System.InvalidOperationException(""nope"");
            }
            return new PartlyThrowing();");

        var formatted = ReturnValueFormatter.Format(value);

        Assert.Contains("\"Fine\":\"ok\"", formatted);
        // Unwrapped from reflection's TargetInvocationException, whose name says nothing about the cause.
        Assert.Contains("\"Broken\":\"<getter threw InvalidOperationException>\"", formatted);
    }

    [Fact]
    public void ThrowingEnumerable_KeepsTheItemsItAlreadyYielded_AndSaysWhereItStopped()
    {
        var formatted = ReturnValueFormatter.Format(ThrowAfter(2));

        Assert.Contains("0,1,", formatted);
        Assert.Contains("<enumeration threw InvalidOperationException after 2 items>", formatted);
    }

    // ------------------------------------------------------------------------------------------
    // Bounds. Each one must report itself -- a silent truncation would recreate the reported bug.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Depth alone does not cover this: a cycle would still emit MaxDepth levels of the same two objects
    /// before stopping, describing a shape that does not exist. A self-containing list is the cheapest
    /// real cycle in the BCL and exercises the same ancestor stack every other path shares.
    /// </summary>
    [Fact]
    public void CircularReference_IsNamedRatherThanRecursed()
    {
        var outer = new List<object> { "a" };
        var inner = new List<object> { outer };
        outer.Add(inner);

        var formatted = ReturnValueFormatter.Format(outer);

        Assert.Contains("circular reference", formatted);
        Assert.True(formatted.Length < 200, "a cycle must terminate immediately, not unwind to MaxDepth; got: " + formatted);
    }

    [Fact]
    public void NestingDeeperThanMaxDepth_StopsAndSaysSo()
    {
        object head = "end";
        for (var i = 0; i < ReturnValueFormatter.MaxDepth + 4; i++)
        {
            head = new List<object> { head };
        }

        var formatted = ReturnValueFormatter.Format(head);

        Assert.Contains("max depth " + ReturnValueFormatter.MaxDepth + " reached", formatted);
        Assert.DoesNotContain("end", formatted);
    }

    [Fact]
    public void CollectionLongerThanMaxCollectionItems_IsTruncatedAndSaysSo()
    {
        var total = ReturnValueFormatter.MaxCollectionItems + 25;

        var formatted = ReturnValueFormatter.Format(Enumerable.Range(0, total).ToList());

        Assert.Contains("<truncated: more than " + ReturnValueFormatter.MaxCollectionItems + " items; the rest are not shown>", formatted);
        Assert.DoesNotContain((total - 1).ToString(), formatted.Substring(0, formatted.IndexOf("<truncated", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The hazard the item cap actually guards, and the reason it does not first count the total to
    /// report "500 of N": a script can return a lazily-generated sequence that never ends, and this code
    /// runs on Revit's UI thread. Enumerating it to completion would wedge Revit -- strictly worse than
    /// the bug being fixed, which merely printed a type name. If this test hangs, it has failed.
    /// </summary>
    [Fact]
    public async Task InfiniteSequence_IsBoundedRatherThanEnumeratedToCompletion()
    {
        var work = Task.Run(() => ReturnValueFormatter.Format(Forever()));

        var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(20))) == work;

        Assert.True(finished, "formatting an endless sequence must terminate; on Revit's UI thread this hangs the whole application");
        Assert.Contains("<truncated", await work);
    }

    [Fact]
    public void NestedStringLongerThanMaxStringLength_IsTruncatedWithACount()
    {
        var value = new { Big = new string('y', ReturnValueFormatter.MaxStringLength + 10) };

        var formatted = ReturnValueFormatter.Format(value);

        Assert.Contains("<truncated: 10 more characters>", formatted);
    }

    /// <summary>
    /// The bound that actually stops a pathological graph: MaxCollectionItems is per-collection, so a
    /// nested structure could otherwise multiply out to MaxCollectionItems^MaxDepth values while every
    /// individual limit held. 60 x 60 x 60 = 216,000 nodes against a 5,000 budget.
    /// </summary>
    [Fact]
    public void GraphExceedingTheTotalNodeBudget_StopsAndSaysSo()
    {
        var value = Enumerable.Range(0, 60)
            .Select(_ => Enumerable.Range(0, 60).Select(_ => Enumerable.Range(0, 60).ToList()).ToList())
            .ToList();

        var formatted = ReturnValueFormatter.Format(value);

        Assert.Contains("serialization budget ran out", formatted);
        // The budget is on VALUES, not characters, so assert the real property: the walk stopped early.
        Assert.True(formatted.Length < CharacterCeiling, "expected the node budget to stop the walk long before the full 216,000-value graph was emitted; got " + formatted.Length + " characters");
    }

    /// <summary>
    /// The connector's own markers are charged to the character budget too. `return listOfElements;` is
    /// the exact shape a script hits when it forgets to project, and every element produces a ~200-char
    /// "no display form" marker -- so if markers bypassed the budget (they did, at first), 500 of them
    /// blew past the documented 64 KB by an order of magnitude while every individual limit still held.
    /// </summary>
    [Fact]
    public void ManyValuesWithNoDisplayForm_StopAtTheSharedCharacterBudget()
    {
        var value = Enumerable.Range(0, ReturnValueFormatter.MaxCollectionItems).Select(_ => new OpaqueThing()).ToList();

        var formatted = ReturnValueFormatter.Format(value);

        Assert.Contains("no display form", formatted);
        Assert.True(
            formatted.Length < CharacterCeiling,
            "markers must be charged to the shared character budget; got " + formatted.Length + " characters");
    }

    /// <summary>
    /// Truncation cuts at a UTF-16 CODE UNIT index, so without a guard it lands between a surrogate pair
    /// and leaves a lone high surrogate.
    ///
    /// <para>The independent review expected that to THROW out of Utf8JsonWriter and lose the whole
    /// return value. Measured, it does not -- reverting the guard leaves every other assertion here
    /// passing, so the dramatic claim is not what this test can pin. What IS observable is the cut
    /// position itself: the guard backs the cut off by one code unit, so the reported remainder differs by
    /// exactly one. Asserting the exact count is what makes this test fail when the guard is removed;
    /// asserting "it round-trips" would have passed either way, which is the trap the house rule exists
    /// to catch.</para>
    /// </summary>
    [Fact]
    public void TruncatingAStringOfNonBmpCharacters_DoesNotSplitASurrogatePair()
    {
        // Each astral character is two code units, and the leading "x" puts every high surrogate on an
        // odd index -- so the 8 KB cut lands exactly mid-pair.
        var astral = string.Concat(Enumerable.Repeat("\U0001F600", ReturnValueFormatter.MaxStringLength));
        var value = new { Big = "x" + astral };
        var total = 1 + (2 * ReturnValueFormatter.MaxStringLength);

        var formatted = ReturnValueFormatter.Format(value);

        // Kept MaxStringLength - 1 code units, not MaxStringLength: one fewer, because the last one would
        // have been half a character.
        Assert.Contains("<truncated: " + (total - (ReturnValueFormatter.MaxStringLength - 1)) + " more characters>", formatted);
        using var parsed = System.Text.Json.JsonDocument.Parse(formatted);
        Assert.NotNull(parsed.RootElement.GetProperty("Big").GetString());
    }

    /// <summary>
    /// The volume bounds and the time bound are not the same bound (independent review). A sequence well
    /// inside every count limit can still be slow per item -- a Revit property getter doing real work is
    /// the live case -- and this runs on the UI thread after the run is already complete, past any
    /// max_duration_ms cancellation. 300 items x 25 ms would be 7.5 seconds of wedged Revit.
    /// </summary>
    [Fact]
    public void SlowButBoundedSequence_StopsAtTheTimeBudget()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        var formatted = ReturnValueFormatter.Format(SlowSequence(300, TimeSpan.FromMilliseconds(25)));

        Assert.True(
            started.Elapsed < ReturnValueFormatter.TimeLimit + TimeSpan.FromSeconds(3),
            "formatting must stop at the time budget; took " + started.Elapsed);
        Assert.Contains("<truncated", formatted);
    }

    /// <summary>
    /// A per-object member cap, not just the whole-graph node budget: one very wide object could otherwise
    /// spend the entire 5,000-value allowance by itself and leave nothing for the rest of the graph.
    /// </summary>
    [Fact]
    public void ObjectWithMoreMembersThanTheCap_IsTruncatedAndSaysSo()
    {
        var properties = string.Join(" ", Enumerable.Range(0, ReturnValueFormatter.MaxCollectionItems + 5).Select(i => "public int P" + i + " { get; set; }"));
        var value = RunScriptReturning("class Wide { " + properties + " } return new Wide();");

        var formatted = ReturnValueFormatter.Format(value);

        Assert.Contains("more than " + ReturnValueFormatter.MaxCollectionItems + " members", formatted);
    }

    [Fact]
    public void ManyLargeStrings_StopAtTheSharedCharacterBudget()
    {
        var chunk = new string('z', 4096);
        var value = Enumerable.Range(0, 100).Select(_ => chunk).ToList();

        var formatted = ReturnValueFormatter.Format(value);

        // 100 x 4096 = 409,600 characters of content against a 64 KB budget.
        Assert.True(formatted.Length < CharacterCeiling, "expected the shared character budget to bound the output; got " + formatted.Length + " characters");
    }

    /// <summary>
    /// The ceiling every character-budget test below asserts against, and the reason it is not simply
    /// <see cref="ReturnValueFormatter.MaxCharacters"/>. Two things are legitimately not charged: JSON
    /// structural punctuation (braces, commas, quotes) and the overshoot of the one value that crossed the
    /// line, which is emitted whole rather than cut at exactly zero. So the honest guarantee is "64 KB of
    /// content plus one value plus punctuation", and asserting a round 64 KB would fail for a correct
    /// implementation. Independent review's point stands though: the earlier assertions were calibrated at
    /// double the documented number and would have passed with the budget bypassed entirely.
    /// </summary>
    private static int CharacterCeiling =>
        ReturnValueFormatter.MaxCharacters + ReturnValueFormatter.MaxStringLength + (4 * ReturnValueFormatter.MaxCollectionItems);

    // ------------------------------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------------------------------

    private sealed class Projection
    {
        public string Name { get; set; } = "";
        public int Count;
    }

    private sealed class OpaqueThing
    {
    }

    private sealed class UsefulToString
    {
        public override string ToString() => "I am useful";
    }

    private sealed class ThrowingToString
    {
        public override string ToString() => throw new InvalidOperationException("secret");
    }

    /// <summary>
    /// Compiles and runs a real script through the production runner, so tests that depend on a
    /// script-defined type get the genuine article: a type in an assembly Roslyn emitted to memory.
    /// The Revit metadata references are required for ScriptGlobals' own members to bind at all (see
    /// RoslynScriptRunnerTests' static constructor for the full reasoning) even though these scripts
    /// touch none of them.
    /// </summary>
    private static object RunScriptReturning(string script)
    {
        var runner = new RoslynScriptRunner(additionalMetadataReferences: RevitApiReference.References);
        var globals = new ScriptGlobals(
            document: new FakeDocumentAdapter(),
            uiApplication: new FakeUiApplicationAdapter(),
            uiDocument: new FakeUiDocumentAdapter(),
            cancellationToken: CancellationToken.None);

        var outcome = runner.RunAsync(script, globals, CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(outcome.Success, "the fixture script failed to run: " + outcome.Exception);
        Assert.NotNull(outcome.ReturnValue);
        return outcome.ReturnValue!;
    }

    /// <summary>Stands in for a collection whose per-item cost is real work -- a Revit property getter.</summary>
    private static IEnumerable<int> SlowSequence(int count, TimeSpan perItem)
    {
        for (var i = 0; i < count; i++)
        {
            Thread.Sleep(perItem);
            yield return i;
        }
    }

    private static IEnumerable<int> Forever()
    {
        var i = 0;
        while (true)
        {
            yield return i++;
        }
    }

    private static IEnumerable<int> ThrowAfter(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return i;
        }

        throw new InvalidOperationException("enumeration blew up");
    }
}
