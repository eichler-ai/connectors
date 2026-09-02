//go:build harness

// Tier-2 coverage for issue #91's headline claim: the connector's own script API is indexed as an
// add-in API and returned by the discovery tools beside Autodesk's own.
//
// This is the ONE claim in #91 with no automated coverage at all before this file (issue #93, item 2).
// Everything else is guarded at tier 1 -- the facade's public surface, the doc-comment content, the
// namespace, the collision set -- but the step that makes the API actually reachable by an agent is
// BridgeHost.CollectAssembliesToSync registering the assembly as ("addin", ...), and that is a private
// method on an internal class in a project with no test assembly. It ran only in live verification,
// which is to say: it was checked once, by hand, and nothing would notice if it regressed.
//
// Tier 2 is the right level rather than a workaround. The claim is "an agent can find these", and the
// only thing that can answer it is a real add-in in a real Revit process, synced into a real SQLite
// cache, answering over the real wire. A unit test over CollectAssembliesToSync would assert that we
// call a method, not that discovery works.
package harness_test

import (
	"encoding/json"
	"slices"
	"sort"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/revit/test-harness/mcpclient"
)

const (
	connectorNamespace = "Eichler.Connectors.Revit"
	connectorTypeName  = "Connector"
)

// connectorMembers is the connector's script API, written out by hand and deliberately duplicated from
// MCPBridge.Core.Tests' ConnectorApiSurfaceTests. The duplication is the point: this asserts what an
// AGENT receives over the wire, which is a different question from what the assembly contains, and the
// two are only equal while every layer between them works. Deriving one from the other would collapse
// exactly the gap this file exists to cover.
var connectorMembers = []string{
	"CreateFamilyDocument",
	"CreateProjectDocument",
	"DialogResultOverrides",
	"ExportsDirectory",
	"ImportsDirectory",
	"OpenForWriting",
	"Publish",
	"Settle",
	"WithTransaction",
	"WithoutTransaction",
}

type searchFunctionsResult struct {
	Results []struct {
		Namespace     string  `json:"namespace"`
		Name          string  `json:"name"`
		DeclaringType string  `json:"declaring_type"`
		Score         float64 `json:"score"`
	} `json:"results"`
}

type listFunctionsResult struct {
	Namespace string `json:"namespace"`
	Type      string `json:"type"`
	Members   string `json:"members"`
	Types     string `json:"types"`
}

func callListFunctions(t *testing.T, c *mcpclient.Client, args map[string]any) json.RawMessage {
	t.Helper()
	raw, err := c.CallTool("list_functions", args, 15*time.Second)
	if err != nil {
		t.Fatalf("list_functions: %v", err)
	}
	return raw
}

func TestConnectorApiIsDiscoverable(t *testing.T) {
	c, instances := startClient(t)
	instanceID := instances.Instances[0].InstanceID

	t.Run("ListFunctionsReturnsExactlyTheConnectorApi", func(t *testing.T) {
		out := decodeToolResult[listFunctionsResult](t, callListFunctions(t, c, map[string]any{
			"instance_id": instanceID,
			"namespace":   connectorNamespace,
			"type_name":   connectorTypeName,
		}))

		// Sorted slice equality, not a count plus Contains-per-member. Review found the latter could not
		// catch a rename whose old name is a substring of the new one -- Publish -> PublishFile,
		// OpenForWriting -> OpenForWritingAsync -- because Contains ran against the whole joined string
		// and the count stayed 7. Those are the spellings a maintainer would actually use, so the guard
		// was open on its most likely mutation. Equality also covers duplicates, extras, ordering and
		// whitespace in one comparison, which is what "exactly" in the test name claims.
		got := strings.Split(out.Members, ", ")
		sort.Strings(got)
		want := append([]string(nil), connectorMembers...)
		sort.Strings(want)
		if !slices.Equal(got, want) {
			t.Fatalf("list_functions did not return exactly the connector API.\n got: %v\nwant: %v\n(raw: %q)",
				got, want, out.Members)
		}
	})

	// The runtime seam must NOT be visible. It is internal, and DiscoveryReflector indexes publicly
	// visible types -- but that is a property of the built assembly, and this asserts what actually
	// reached the agent-facing corpus. Anything else appearing here means plumbing is being advertised.
	t.Run("TheRuntimeSeamIsNotIndexed", func(t *testing.T) {
		out := decodeToolResult[listFunctionsResult](t, callListFunctions(t, c, map[string]any{
			"instance_id": instanceID,
			"namespace":   connectorNamespace,
		}))

		if strings.Contains(out.Types, "IConnectorRuntime") {
			t.Errorf("IConnectorRuntime is indexed and visible to an agent; it must stay internal. types=%q", out.Types)
		}
		if strings.TrimSpace(out.Types) != connectorTypeName {
			t.Errorf("expected %q to be exactly one type (%s), got %q",
				connectorNamespace, connectorTypeName, out.Types)
		}
	})

	// The reason the sidecar has to be deployed beside the DLL: DiscoveryReflector treats a MISSING
	// sidecar as "everything is documented", so a DLL-only deploy yields a fully browsable API whose
	// summaries are all empty -- which looks like working discovery. Only a live call can tell the two
	// apart, because both produce a well-formed response.
	//
	// Covers ALL SEVEN members, not just Publish. The first version described only Publish, so a sidecar
	// covering some members and not others would have passed (review finding).
	t.Run("DescribeFunctionReturnsRealDocumentationForEveryMember", func(t *testing.T) {
		// NOTE ON WHAT THIS DOES NOT CHECK. An earlier version asserted the summary did not contain
		// ".Never" or ".Order" -- literal fragments of one member's then-current prose. That was doubly
		// weak: ".Order" was already dead (it came from OpenForWriting's old text, which this subtest
		// never fetched), and ".Never" would go vacuous the moment anyone reworded Publish.
		//
		// Replacing it with a general `[a-z][.!?][A-Z]` regex was worse, and running it live is what
		// showed why: it fires on every dotted identifier in ordinary prose -- "System.IO",
		// "UIApplication.Application", "Document.LoadFamily" -- which is indistinguishable from a
		// paragraph join once the text is rendered. Five of seven summaries "failed".
		//
		// So the paragraph-separator property is pinned at TIER 1 instead, in ConnectorApiSurfaceTests,
		// where the XML is still structured and the check can be exact: for each adjacent block pair it
		// compares the rendered text against the actual last/first words of those blocks. This subtest
		// keeps the job only a live call can do -- proving the sidecar is deployed and carries real text.
		for _, member := range connectorMembers {
			out := describeFunctionSuccess(t, callDescribeFunction(t, c, map[string]any{
				"instance_id": instanceID,
				"member":      connectorNamespace + "." + connectorTypeName + "." + member,
			}))

			// An OVERLOADED member answers with its overload list instead of a summary, by
			// describe_function's own contract -- re-call with each member_id (#146 Phase 0 made
			// WithTransaction the first connector member with two overloads). Every overload must
			// carry real text: an undocumented one would otherwise hide behind its sibling.
			results := []map[string]any{out.Result}
			if overloads, ok := out.Result["overloads"].([]any); ok && len(overloads) > 0 {
				results = results[:0]
				for _, o := range overloads {
					memberID, _ := o.(map[string]any)["member_id"].(string)
					if memberID == "" {
						t.Errorf("%s: an overloads[] entry carries no member_id, so it cannot be described: %+v", member, o)
						continue
					}
					results = append(results, describeFunctionSuccess(t, callDescribeFunction(t, c, map[string]any{
						"instance_id": instanceID,
						"member_id":   memberID,
					})).Result)
				}
			}

			for _, result := range results {
				summary, _ := result["summary"].(string)
				if strings.TrimSpace(summary) == "" {
					t.Errorf("%s has an empty summary live -- the XML doc sidecar is probably not deployed "+
						"beside Eichler.Connectors.Revit.dll. result=%+v", member, result)
					continue
				}

				// The summaries are agent-facing product; maintainer vocabulary in them is a defect (D5).
				for _, forbidden := range []string{"ScriptGlobals", "PRD §", "IConnectorRuntime", "issue #"} {
					if strings.Contains(summary, forbidden) {
						t.Errorf("%s leaks maintainer vocabulary %q to an agent: %q", member, forbidden, summary)
					}
				}
			}
		}
	})

	// Exact-name resolution. Deliberately does NOT assert anything about ranking relative to Revit's
	// own API, and the reason is worth recording because the first version of this subtest was named
	// "...AndItIsIndexedAsAnAddin" and its comment claimed "ranked BELOW Revit's own API, per PRD §08".
	//
	// Review pointed out that nothing here could catch the ("addin", ...) -> ("core", ...) mutation, and
	// suggested asserting that a core member outranks the connector's. Measured live before writing
	// that, and it is false: `search_functions "Publish"` puts Eichler...Connector.Publish at 636.95,
	// ABOVE Autodesk.Revit.DB.Document.PublishCoordinates at 591.8. The connector wins on an exact
	// member-name match in tier 2, which no assembly kind changes.
	//
	// What `kind` actually does is narrower than "ranked below": a +0.5 CoreBoost (DiscoveryCache.cs)
	// and a tie-break inside the FTS tier's ORDER BY. It is not serialised onto the wire at all, so an
	// addin/core mislabel is not observable through the tools -- and an assertion pretending otherwise
	// would be exactly the kind of guard this suite keeps having to delete. The registration ITSELF is
	// pinned: removing it makes every subtest above fail, because nothing gets indexed.
	t.Run("ExactNameSearchResolvesTheConnectorMember", func(t *testing.T) {
		raw, err := c.CallTool("search_functions", map[string]any{
			"instance_id": instanceID,
			"query":       connectorTypeName + ".Publish",
			"top_n":       3,
		}, 15*time.Second)
		if err != nil {
			t.Fatalf("search_functions: %v", err)
		}

		out := decodeToolResult[searchFunctionsResult](t, raw)

		if len(out.Results) == 0 {
			t.Fatal("search_functions returned nothing for an exact Type.Member query")
		}
		top := out.Results[0]
		if top.Namespace != connectorNamespace || top.Name != "Publish" {
			t.Errorf("expected %s.%s.Publish as the top hit for its own exact name, got %s.%s",
				connectorNamespace, connectorTypeName, top.Namespace, top.Name)
		}
	})
}
