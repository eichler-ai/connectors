using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Eichler.Connectors.Revit;
using MCPBridge.Core.Discovery;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

/// <summary>
/// Issue #91: the connector's own script API is now a discoverable add-in API -- one public type in its
/// own assembly, indexed by list_functions/search_functions/describe_function beside Autodesk's, under a
/// namespace that says whose it is.
///
/// <para>What that makes newly fragile is DOCUMENTATION, not behaviour. The XML doc comments on
/// <see cref="Connector"/> are not commentary any more; they are the product, shipped verbatim to an
/// agent by describe_function. Nothing in the compiler notices when a summary is absent, is written for a
/// maintainer instead of an agent, or grows into an essay.</para>
///
/// <para>These tests therefore read the actual generated sidecar THROUGH
/// <see cref="XmlDocIndex"/> -- the production parser, on the production file. An earlier version of this
/// class re-parsed the XML with <c>XElement.Value</c>, which review found was a strictly weaker parser
/// than the one that ships: <c>.Value</c> silently drops self-closing elements, so a summary saying
/// <c>&lt;see cref="ScriptGlobals"/&gt;</c> would reach an agent as the literal word "ScriptGlobals"
/// while the test built to forbid that word saw an empty string and passed. Naming an internal type via
/// <c>&lt;see cref&gt;</c> is the way a maintainer would most naturally do it, so that was the guard's
/// primary path, and it was open. The lesson generalises: a test that reimplements the thing it is
/// checking tests the reimplementation.</para>
///
/// <para>Note what these tests never touch: a member's return type or parameter types. Half the surface
/// is typed in <c>Autodesk.Revit.DB.Document</c>, and RevitAPI.dll is mixed-mode C++/CLI that cannot load
/// outside a live Revit process, so resolving a signature here throws. Member NAMES come from reflection,
/// doc-id strings come from the XML's own attributes, and rendered text comes from XmlDocIndex -- none of
/// which forces a signature to resolve.</para>
/// </summary>
public class ConnectorApiSurfaceTests
{
    /// <summary>
    /// The connector's script API, written out by hand -- the same reasoning as
    /// <see cref="ScriptGlobalsDiscoverabilityTests"/>'s expected list: a test that recomputed this by
    /// reflection would pass for any surface at all, including one that accidentally exposed the runtime
    /// seam.
    /// </summary>
    private static readonly string[] ExpectedMembers =
    {
        "CreateFamilyDocument",
        "CreateProjectDocument",
        "DialogResultOverrides",
        "ExportsDirectory",
        "ImportsDirectory",
        "OpenForWriting",
        "Publish",
    };

    private const string ConnectorDocId = "T:Eichler.Connectors.Revit.Connector";

    /// <summary>
    /// Catches the failure mode D5 measured: the pre-#91 summaries ran 80-230 words because one comment
    /// was serving both an agent and a maintainer.
    ///
    /// <para>The headroom is real but not large -- <c>OpenForWriting</c>, the longest, measures around 110
    /// words. An earlier revision of this comment claimed "none of the seven is near it", which was simply
    /// not measured and was wrong. If a summary needs more than this, that is the signal to move something
    /// into <c>&lt;remarks&gt;</c>, which XmlDocIndex does not read.</para>
    /// </summary>
    private const int MaxSummaryWords = 130;

    [Fact]
    public void PublicSurface_IsExactlyTheConnectorApi()
    {
        var actual = typeof(Connector)
            // Static, Field and Event are included DELIBERATELY, and none of them is expected to match.
            // Reflecting only public instance properties and methods -- the previous shape -- meant a
            // `public static string Hint => "..."` on Connector would reach describe_function while the
            // test asserting the surface "is exactly the connector API" stayed green. CS1591 does not
            // catch it either, since a documented new member compiles fine.
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.MemberType is MemberTypes.Property or MemberTypes.Method or MemberTypes.Field or MemberTypes.Event)
            .Where(m => m is not MethodInfo method || !method.IsSpecialName)
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedMembers, actual);

        // If this failed you changed the connector's script API. Update, in this order:
        //   1. the expected list above
        //   2. revit/mcp-server/internal/mcpserver/skill.md -- if the change affects the rules it states
        //   3. every script that calls it: revit/test-harness/*.go, and the validation corpus
        // A new member needs no tool-description or GlobalNames update, and that is the #91 design working
        // as intended: those name the ENTRY POINT (Connector), never its members.
    }

    /// <summary>
    /// The constructor must stay non-public. A script binds <c>Connector</c> from scope and never builds
    /// one; a public constructor would let a script make a Connector over its own IConnectorRuntime, which
    /// is only harmless as long as that interface stays internal too.
    /// </summary>
    [Fact]
    public void ConnectorHasNoPublicConstructor()
    {
        Assert.Empty(typeof(Connector).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    /// <summary>
    /// <see cref="Connector"/> must be the ONLY publicly visible type in its assembly. That is what makes
    /// "an agent sees exactly this and no plumbing" a compile-time fact: DiscoveryReflector indexes
    /// publicly visible types, so anything else made public here lands in the agent-facing corpus.
    /// It is also the reason the assembly exists at all -- MCPBridge.Core has 71 public types, and syncing
    /// it would advertise every one of them.
    /// </summary>
    [Fact]
    public void ConnectorIsTheOnlyPubliclyVisibleTypeInItsAssembly()
    {
        // Type.IsVisible, matching DiscoveryReflector.IsPubliclyVisible EXACTLY. An earlier version used
        // `t.IsPublic || (t.IsNestedPublic && t.DeclaringType.IsPublic)`, which review found walks only ONE
        // level of nesting: a public type nested two deep reports IsNestedPublic true with a DeclaringType
        // whose IsPublic is false (nested types always report IsPublic false), so it was excluded here and
        // indexed in production. Reflecting over the same predicate the production code uses is the whole
        // point -- a hand-rolled approximation of a visibility rule tests the approximation.
        var publicTypes = typeof(Connector).Assembly
            .GetTypes()
            .Where(t => t.IsVisible)
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { ConnectorDocId["T:".Length..] }, publicTypes);
    }

    /// <summary>
    /// The namespace is an agent-facing, effectively permanent identifier (issue #91 D3): it ships in
    /// signed artifacts we do not control after release, and it is the whole signal that these functions
    /// are ours and not Autodesk's. Renaming it is a decision, not a refactor.
    /// </summary>
    [Fact]
    public void NamespaceIsTheDecidedVendorRootedOne()
    {
        Assert.Equal("Eichler.Connectors.Revit", typeof(Connector).Namespace);
    }

    [Fact]
    public void EveryPublicMemberHasAnAgentFacingSummaryInTheShippedSidecar()
    {
        var docs = LoadShippedDocs();

        foreach (var member in ExpectedMembers)
        {
            var entry = FindMemberEntry(docs, member);
            Assert.True(
                entry is not null && !string.IsNullOrWhiteSpace(entry.Summary),
                $"'{member}' has no summary in the generated XML sidecar, so describe_function would " +
                "return it as a bare signature. Add an XML doc comment, or check that " +
                "GenerateDocumentationFile is still set in Eichler.Connectors.Revit.csproj.");

            AssertWithinWordBudget(member, entry!.Summary!);
        }
    }

    /// <summary>
    /// The TYPE's own summary is shipped too -- describe_function returns it for the declaring type, and it
    /// is the first thing an agent browsing the namespace reads. It was entirely unguarded until review
    /// pointed out that the member loop skips it, at which point it measured 129 words against a limit that
    /// did not apply to it.
    /// </summary>
    [Fact]
    public void TheTypeSummaryIsAlsoAgentFacingAndWithinBudget()
    {
        var docs = LoadShippedDocs();

        Assert.True(docs.TryGetValue(ConnectorDocId, out var entry) && !string.IsNullOrWhiteSpace(entry.Summary),
            "Connector's own type summary is missing from the sidecar.");

        AssertWithinWordBudget("the Connector type", entry.Summary!);
    }

    /// <summary>
    /// The measured D5 failure mode was not missing text but WRONG-AUDIENCE text: summaries that cited PRD
    /// sections, issue numbers and internal type names an agent cannot see, act on, or look up. Those read
    /// as authoritative and send an agent nowhere.
    ///
    /// <para>Checks <c>&lt;param&gt;</c> and <c>&lt;returns&gt;</c> as well as <c>&lt;summary&gt;</c>, and
    /// the type's summary as well as each member's, because describe_function ships all of them. Only
    /// summaries were checked until review pointed out that <c>ScriptGlobals</c> in a
    /// <c>&lt;param&gt;</c> would sail straight through.</para>
    /// </summary>
    [Theory]
    [InlineData("PRD §")]
    [InlineData("issue #")]
    [InlineData("ScriptGlobals")]
    [InlineData("ManagedDocumentTransactions")]
    [InlineData("IConnectorRuntime")]
    [InlineData("ScriptApiDenylist")]
    [InlineData("TransactionScriptExecutor")]
    public void NoShippedTextLeaksMaintainerVocabulary(string forbidden)
    {
        foreach (var (docId, entry) in LoadShippedDocs())
        {
            foreach (var (what, text) in ShippedTextOf(entry))
            {
                Assert.False(
                    text.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"{docId} ({what}) mentions '{forbidden}'. describe_function ships this to an agent, " +
                    "which can neither read our source nor open our issue tracker.");
            }
        }
    }

    private static IEnumerable<(string What, string Text)> ShippedTextOf(XmlDocEntry entry)
    {
        if (entry.Summary is not null)
        {
            yield return ("summary", entry.Summary);
        }

        if (entry.Returns is not null)
        {
            yield return ("returns", entry.Returns);
        }

        foreach (var (name, text) in entry.Parameters)
        {
            yield return ($"param {name}", text);
        }
    }

    private static void AssertWithinWordBudget(string what, string summary)
    {
        var words = summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(
            words <= MaxSummaryWords,
            $"'{what}' has a {words}-word summary (limit {MaxSummaryWords}). This is shipped verbatim to " +
            "an agent by describe_function; move the rationale to <remarks>, which XmlDocIndex does not read.");
    }

    private static XmlDocEntry? FindMemberEntry(IReadOnlyDictionary<string, XmlDocEntry> docs, string memberName)
    {
        // "M:Eichler.Connectors.Revit.Connector.Publish(System.String,System.String)" -> "Publish".
        var prefix = "Eichler.Connectors.Revit.Connector." + memberName;
        return docs
            .Where(kv => kv.Key.Length > 2 && kv.Key[2..].StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => kv.Value)
            .FirstOrDefault();
    }

    /// <summary>
    /// Every doc entry for this assembly, keyed by doc id, with text rendered by the PRODUCTION parser.
    ///
    /// <para>Doc-id strings are read straight off the XML's <c>name</c> attributes rather than built with
    /// <c>XmlDocId.GetDocId</c>, because building one means resolving parameter types and half of them are
    /// Revit types this test host cannot load. The text itself then comes from
    /// <see cref="XmlDocIndex"/>, so what is asserted is exactly what describe_function would return.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, XmlDocEntry> LoadShippedDocs()
    {
        var assemblyPath = typeof(Connector).Assembly.Location;
        var sidecarPath = Path.ChangeExtension(assemblyPath, ".xml");

        Assert.True(
            File.Exists(sidecarPath),
            $"No XML doc sidecar next to {Path.GetFileName(assemblyPath)}. Without it DiscoveryReflector " +
            "treats every member as documented and returns empty summaries -- see GenerateDocumentationFile " +
            "in Eichler.Connectors.Revit.csproj, and the .xml must be deployed beside the .dll.");

        // Existence is not enough, and this is not paranoia: MSBuild never deletes orphaned files from an
        // output directory without a `clean`. Turn GenerateDocumentationFile off and rebuild incrementally
        // and the PREVIOUS sidecar stays right here, so every assertion in this file would pass against a
        // stale file describing code that no longer exists -- which is precisely the scenario this class
        // claims to catch. Review caught that the mutation test for it was only valid because a clean
        // happened to precede the rebuild.
        // The tolerance is not slop: the compiler writes the .xml and the .dll in the same compile, a few
        // milliseconds apart and in that order (measured: ~11ms), so a strict >= comparison fails on a
        // perfectly fresh build. What this catches is a sidecar left over from an APPRECIABLY earlier
        // build -- the shape that matters, since it persists for as long as nobody runs a clean.
        //
        // Stated honestly: it does not catch a rebuild that happens within the tolerance of the previous
        // one. The count and per-member assertions above are the real backstop for that, since a sidecar
        // predating a surface change fails them; this check exists for the case they cannot see, where the
        // surface is unchanged and only doc generation stopped.
        var staleness = File.GetLastWriteTimeUtc(assemblyPath) - File.GetLastWriteTimeUtc(sidecarPath);
        Assert.True(
            staleness < TimeSpan.FromSeconds(30),
            $"'{Path.GetFileName(sidecarPath)}' is {staleness.TotalSeconds:F0}s older than the assembly " +
            "beside it, so it is stale output from an earlier build and describes code that may no longer " +
            "exist. Run a clean build, or check that GenerateDocumentationFile is still set.");

        var index = XmlDocIndex.LoadFromFile(sidecarPath);
        var docs = new Dictionary<string, XmlDocEntry>(StringComparer.Ordinal);

        foreach (var member in XDocument.Load(sidecarPath).Root?.Element("members")?.Elements("member")
                               ?? Enumerable.Empty<XElement>())
        {
            var docId = (string?)member.Attribute("name");
            if (docId is null || !docId.Contains("Eichler.Connectors.Revit.Connector.", StringComparison.Ordinal)
                              && docId != ConnectorDocId)
            {
                continue;
            }

            // The internal constructor has a doc comment, so the compiler emits it, but DiscoveryReflector
            // only ever reflects PUBLIC members -- it never reaches an agent, and holding it to the
            // agent-facing rules would be checking text that is not the product.
            if (docId.Contains(".#ctor", StringComparison.Ordinal))
            {
                continue;
            }

            if (index.TryGet(docId, out var entry))
            {
                docs[docId] = entry;
            }
        }

        // Guards the vacuous pass. NoShippedTextLeaksMaintainerVocabulary iterates this dictionary, so an
        // empty one would make all seven of its cases assert nothing at all and report green. That is
        // reachable without touching this file: rename the namespace, rename the assembly, or change the
        // sidecar's shape. Asserting the count here means the Theory is protected by its own helper rather
        // than by a sibling test someone might later edit.
        //
        // Seven members plus the type's own "T:" entry; the internal constructor is filtered out above.
        Assert.Equal(ExpectedMembers.Length + 1, docs.Count);

        return docs;
    }
}
