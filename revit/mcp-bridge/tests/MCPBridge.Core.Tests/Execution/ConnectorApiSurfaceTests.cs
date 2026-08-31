using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Eichler.Connectors.Revit;
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
/// maintainer instead of an agent, or grows into an essay -- so these tests read the actual generated
/// sidecar, which is the same file DiscoveryReflector joins against at runtime.</para>
///
/// <para>Deliberately reads the SIDECAR rather than the source: what ships to an agent is the .xml
/// MSBuild generated and copied next to the .dll. A test over the source text would pass in the one
/// scenario that matters most -- GenerateDocumentationFile quietly stopping, which produces perfectly
/// good comments and empty summaries.</para>
///
/// <para>Note what these tests never touch: a member's return type or parameter types. Half the surface
/// is typed in <c>Autodesk.Revit.DB.Document</c>, and RevitAPI.dll is mixed-mode C++/CLI that cannot load
/// outside a live Revit process, so resolving a signature here throws. Names come from reflection, types
/// come from the XML's own doc-id strings, and nothing forces a signature to resolve.</para>
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

    /// <summary>
    /// Long enough that none of the seven is near it, short enough to catch the actual failure mode D5
    /// measured: the pre-#91 summaries ran 80-230 words because one comment was serving both an agent and
    /// a maintainer. Rationale belongs in &lt;remarks&gt; (which XmlDocIndex does not read) or beside the
    /// implementation in Core.
    /// </summary>
    private const int MaxSummaryWords = 130;

    [Fact]
    public void PublicSurface_IsExactlyTheConnectorApi()
    {
        var actual = typeof(Connector)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.MemberType is MemberTypes.Property or MemberTypes.Method)
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
    /// <see cref="Connector"/> must be the ONLY publicly visible type in its assembly. That is what makes
    /// "an agent sees exactly this and no plumbing" a compile-time fact: DiscoveryReflector indexes
    /// publicly visible types, so anything else made public here lands in the agent-facing corpus.
    /// It is also the reason the assembly exists at all -- MCPBridge.Core has 71 public types, and syncing
    /// it would advertise every one of them.
    /// </summary>
    [Fact]
    public void ConnectorIsTheOnlyPubliclyVisibleTypeInItsAssembly()
    {
        var publicTypes = typeof(Connector).Assembly
            .GetTypes()
            .Where(t => t.IsPublic || (t.IsNestedPublic && (t.DeclaringType?.IsPublic ?? false)))
            .Select(t => t.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Eichler.Connectors.Revit.Connector" }, publicTypes);
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
        var summaries = LoadSidecarSummaries();

        foreach (var member in ExpectedMembers)
        {
            Assert.True(
                summaries.TryGetValue(member, out var summary) && !string.IsNullOrWhiteSpace(summary),
                $"'{member}' has no summary in the generated XML sidecar, so describe_function would " +
                "return it as a bare signature. Add an XML doc comment, or check that " +
                "GenerateDocumentationFile is still set in Eichler.Connectors.Revit.csproj.");

            var words = summary!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.True(
                words <= MaxSummaryWords,
                $"'{member}' has a {words}-word summary (limit {MaxSummaryWords}). This is shipped " +
                "verbatim to an agent by describe_function; move the rationale to <remarks>, which " +
                "XmlDocIndex does not read.");
        }
    }

    /// <summary>
    /// The measured D5 failure mode was not missing text but WRONG-AUDIENCE text: summaries that cited PRD
    /// sections, issue numbers and internal type names an agent cannot see, act on, or look up. Those read
    /// as authoritative and send an agent nowhere.
    /// </summary>
    [Theory]
    [InlineData("PRD §")]
    [InlineData("issue #")]
    [InlineData("ScriptGlobals")]
    [InlineData("ManagedDocumentTransactions")]
    [InlineData("IConnectorRuntime")]
    [InlineData("ScriptApiDenylist")]
    [InlineData("TransactionScriptExecutor")]
    public void NoSummaryLeaksMaintainerVocabulary(string forbidden)
    {
        foreach (var (member, summary) in LoadSidecarSummaries())
        {
            Assert.False(
                summary.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"'{member}' mentions '{forbidden}'. describe_function ships this summary to an agent, " +
                "which can neither read our source nor open our issue tracker.");
        }
    }

    /// <summary>
    /// Parses the generated sidecar into member-name -> summary. Keyed by the bare member NAME rather than
    /// the full doc id, because building a doc id would mean resolving parameter types, and half of them
    /// are Revit types this test host cannot load.
    /// </summary>
    private static IReadOnlyDictionary<string, string> LoadSidecarSummaries()
    {
        var assemblyPath = typeof(Connector).Assembly.Location;
        var sidecarPath = Path.ChangeExtension(assemblyPath, ".xml");

        Assert.True(
            File.Exists(sidecarPath),
            $"No XML doc sidecar next to {Path.GetFileName(assemblyPath)}. Without it DiscoveryReflector " +
            "treats every member as documented and returns empty summaries -- see GenerateDocumentationFile " +
            "in Eichler.Connectors.Revit.csproj, and the .xml must be deployed beside the .dll.");

        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in XDocument.Load(sidecarPath).Root?.Element("members")?.Elements("member")
                               ?? Enumerable.Empty<XElement>())
        {
            var docId = (string?)member.Attribute("name");
            var summary = member.Element("summary")?.Value;
            if (docId is null || summary is null)
            {
                continue;
            }

            // "M:Eichler.Connectors.Revit.Connector.Publish(System.String,System.String)" -> "Publish".
            // Only members OF Connector: the type's own "T:" entry and anything internal are skipped.
            var match = Regex.Match(docId, @"^[MPF]:Eichler\.Connectors\.Revit\.Connector\.([A-Za-z0-9_]+)");
            if (match.Success)
            {
                summaries[match.Groups[1].Value] = Regex.Replace(summary, @"\s+", " ").Trim();
            }
        }

        return summaries;
    }
}
