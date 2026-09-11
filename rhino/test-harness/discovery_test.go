//go:build harness

package harness_test

import (
	"encoding/json"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/rhino/test-harness/mcpclient"
)

// End-to-end API discovery (PRD §09) against the running Rhino: the server's
// MCP tools reach the plug-in's live discovery cache (RhinoCommon reflected on
// load) and, for search, the broker's own semsearch index built by paging
// dump_members. Requires the discovery-enabled bridge deployed (PR1).

type nsEntry struct {
	Namespace string `json:"namespace"`
	TypeCount int    `json:"type_count"`
}
type listFunctionsOut struct {
	Namespaces   []nsEntry `json:"namespaces"`
	Types        string    `json:"types"`
	Type         string    `json:"type"`
	Members      string    `json:"members"`
	Namespace    string    `json:"namespace"`
	RhinoVersion string    `json:"rhino_version"`
	Error        *notice   `json:"error"`
}
type discMember struct {
	MemberID      string `json:"member_id"`
	Kind          string `json:"kind"`
	Namespace     string `json:"namespace"`
	DeclaringType string `json:"declaring_type"`
	Name          string `json:"name"`
	Signature     string `json:"signature"`
	Summary       string `json:"summary"`
}
type searchFunctionsOut struct {
	Results      []discMember `json:"results"`
	TotalMatched int          `json:"total_matched"`
	RhinoVersion string       `json:"rhino_version"`
	Ranker       string       `json:"ranker"`
	Error        *notice      `json:"error"`
}
type describeFunctionOut struct {
	Result       map[string]any `json:"result"`
	RhinoVersion string         `json:"rhino_version"`
	Error        *notice        `json:"error"`
}

func discCall[T any](t *testing.T, c *mcpclient.Client, name string, args map[string]any) (T, bool) {
	t.Helper()
	raw, err := c.CallTool(name, args, 30*time.Second)
	if err != nil {
		t.Fatalf("%s: %v", name, err)
	}
	var tr toolResult
	json.Unmarshal(raw, &tr)
	var out T
	src := tr.StructuredContent
	if len(src) == 0 && len(tr.Content) > 0 {
		src = json.RawMessage(tr.Content[0].Text)
	}
	if err := json.Unmarshal(src, &out); err != nil {
		t.Fatalf("decode %s: %v\n%s", name, err, src)
	}
	return out, tr.IsError
}

func TestDiscoveryListFunctionsSurfacesRhinoGeometry(t *testing.T) {
	c := startServer(t)
	waitForInstance(t, c)
	out, isErr := discCall[listFunctionsOut](t, c, "list_functions", map[string]any{})
	if isErr {
		t.Fatalf("%+v", out.Error)
	}
	var found bool
	for _, n := range out.Namespaces {
		if n.Namespace == "Rhino.Geometry" {
			found = true
		}
	}
	if !found {
		t.Fatalf("Rhino.Geometry not in the namespace list (%d namespaces)", len(out.Namespaces))
	}
	if out.RhinoVersion == "" {
		t.Fatal("expected the broker to stamp rhino_version")
	}
}

func TestDiscoveryListFunctionsTypesInGeometry(t *testing.T) {
	c := startServer(t)
	waitForInstance(t, c)
	out, isErr := discCall[listFunctionsOut](t, c, "list_functions", map[string]any{"namespace": "Rhino.Geometry", "page_size": 5000})
	if isErr {
		t.Fatalf("%+v", out.Error)
	}
	for _, want := range []string{"Sphere", "Mesh", "Circle"} {
		if !strings.Contains(out.Types, want) {
			t.Fatalf("type %q not listed in Rhino.Geometry (got %.200q)", want, out.Types)
		}
	}
}

func TestDiscoveryDescribeFunctionResolvesAMember(t *testing.T) {
	c := startServer(t)
	waitForInstance(t, c)
	out, isErr := discCall[describeFunctionOut](t, c, "describe_function", map[string]any{"member": "Rhino.Geometry.Sphere.Radius"})
	if isErr {
		t.Fatalf("%+v", out.Error)
	}
	// A resolved member carries a summary/signature; the XML sidecar is read live from the bundle.
	if out.Result == nil {
		t.Fatal("no result")
	}
	summary, _ := out.Result["summary"].(string)
	sig, _ := out.Result["signature"].(string)
	if summary == "" && sig == "" {
		t.Fatalf("describe returned neither summary nor signature: %+v", out.Result)
	}
	if out.RhinoVersion == "" {
		t.Fatal("expected rhino_version stamped")
	}
}

func TestDiscoverySearchFunctionsFindsCircle_AndIndexBuilds(t *testing.T) {
	c := startServer(t)
	waitForInstance(t, c)

	// search_functions works immediately via the plug-in's keyword ranker, and becomes the broker's
	// semantic index once it finishes building (paging dump_members over ~17.8k members). Poll until a
	// broker ranker answers, proving the end-to-end index path; accept the fallback until then.
	brokerRankers := map[string]bool{"semantic": true, "semantic-no-rerank": true, "lexical": true}
	var out searchFunctionsOut
	var sawBrokerIndex bool
	deadline := time.Now().Add(120 * time.Second)
	for {
		var isErr bool
		out, isErr = discCall[searchFunctionsOut](t, c, "search_functions", map[string]any{"query": "add a circle to the document"})
		if isErr {
			t.Fatalf("%+v", out.Error)
		}
		if brokerRankers[out.Ranker] {
			sawBrokerIndex = true
			break
		}
		if out.Ranker != "keyword-fallback" {
			t.Fatalf("unexpected ranker %q", out.Ranker)
		}
		if time.Now().After(deadline) {
			break
		}
		time.Sleep(2 * time.Second)
	}

	var foundCircle bool
	for _, m := range out.Results {
		if strings.Contains(m.Name, "Circle") || strings.Contains(m.DeclaringType, "Circle") {
			foundCircle = true
		}
	}
	if !foundCircle {
		t.Fatalf("no Circle-related member in the top results (ranker=%s, %d results)", out.Ranker, len(out.Results))
	}
	if !sawBrokerIndex {
		t.Fatalf("the broker semantic index never became ready within the timeout (last ranker=%s) -- dump_members paging or the index build may be broken", out.Ranker)
	}
	t.Logf("search ranker=%s, %d matched", out.Ranker, out.TotalMatched)
}
