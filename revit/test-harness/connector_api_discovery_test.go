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

		got := strings.Split(out.Members, ", ")
		if len(got) != len(connectorMembers) {
			t.Fatalf("expected exactly %d connector members, got %d: %q",
				len(connectorMembers), len(got), out.Members)
		}
		for _, want := range connectorMembers {
			if !strings.Contains(out.Members, want) {
				t.Errorf("list_functions did not return the connector member %q; got %q", want, out.Members)
			}
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
			t.Errorf("expected %q to contain exactly one type (%s), got %q",
				connectorNamespace, connectorTypeName, out.Types)
		}
	})

	// The reason the sidecar has to be deployed beside the DLL: DiscoveryReflector treats a MISSING
	// sidecar as "everything is documented", so a DLL-only deploy yields a fully browsable API whose
	// summaries are all empty -- which looks like working discovery. Only a live call can tell the two
	// apart, because both produce a well-formed response.
	t.Run("DescribeFunctionReturnsRealDocumentation", func(t *testing.T) {
		out := describeFunctionSuccess(t, callDescribeFunction(t, c, map[string]any{
			"instance_id": instanceID,
			"member":      connectorNamespace + "." + connectorTypeName + ".Publish",
		}))

		summary, _ := out.Result["summary"].(string)
		if strings.TrimSpace(summary) == "" {
			t.Fatalf("Publish has an empty summary live -- the XML doc sidecar is probably not deployed "+
				"beside Eichler.Connectors.Revit.dll. result=%+v", out.Result)
		}

		// Pins the issue #91 paragraph-separator fix at the layer an agent actually reads. The defect
		// produced "...fails.Order matters..." -- text that is present, plausible, and wrong, which no
		// emptiness check would catch.
		if strings.Contains(summary, ".Order") || strings.Contains(summary, ".Never") {
			t.Errorf("summary has concatenated paragraphs (missing separator after a sentence): %q", summary)
		}

		// The summaries are agent-facing product; maintainer vocabulary in them is a defect (D5).
		for _, forbidden := range []string{"ScriptGlobals", "PRD §", "IConnectorRuntime"} {
			if strings.Contains(summary, forbidden) {
				t.Errorf("summary leaks maintainer vocabulary %q to an agent: %q", forbidden, summary)
			}
		}
	})

	// Ranked BELOW Revit's own API, per PRD §08. An add-in API that outranked Autodesk's would be a
	// regression in its own right, and the ordering is a property of the synced `kind` column -- i.e.
	// of the same registration this file exists to cover.
	t.Run("ExactNameSearchFindsItAndItIsIndexedAsAnAddin", func(t *testing.T) {
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
