//go:build harness

package harness_test

import (
	"encoding/json"
	"os/exec"
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
	// Both call shapes (PR4): describe carries the Python call form beside the C# signature, round-tripped
	// live through the broker and the python_call column. Radius is an instance property, so its Python
	// form is bare attribute access.
	if pc, _ := out.Result["python_call"].(string); pc != "Radius" {
		t.Fatalf("python_call for Sphere.Radius = %q, want \"Radius\": %+v", pc, out.Result)
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
	//
	// When the server bundles the ranking models (the dev/release build does), require the index to reach
	// a SEMANTIC ranker -- accepting "lexical" would silently pass a build whose embedder failed to load
	// (review of #295 F5c). Only a genuinely model-less build may settle for lexical.
	modelsBundled := strings.Contains(searchModelsLine(t), "bundled and verified")
	brokerRankers := map[string]bool{"semantic": true, "semantic-no-rerank": true}
	if !modelsBundled {
		brokerRankers["lexical"] = true
	}
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

// searchModelsLine runs the broker exe with -search-models and returns its one-line report, so a test
// can tell a models-bundled build (must reach a semantic ranker) from a model-less one (lexical is fine).
func searchModelsLine(t *testing.T) string {
	t.Helper()
	if *serverExe == "" {
		return ""
	}
	out, err := exec.Command(*serverExe, "-search-models").CombinedOutput()
	if err != nil {
		t.Logf("-search-models exited non-zero (%v); treating as model-less: %s", err, out)
	}
	return string(out)
}

// The rhinoscript kind (PR3): rhinoscriptsyntax functions are indexed with kind=rhinoscript and must
// flow end-to-end through dump_members -> the broker index -> search/describe, beside RhinoCommon.
func TestDiscoveryRhinoScriptFunctionsAreIndexed(t *testing.T) {
	c := startServer(t)
	waitForInstance(t, c)

	// describe_function on a known rs function resolves with its docstring summary.
	d, isErr := discCall[describeFunctionOut](t, c, "describe_function", map[string]any{"member": "rhinoscriptsyntax.AddCircle"})
	if isErr {
		t.Fatalf("describe rhinoscriptsyntax.AddCircle: %+v", d.Error)
	}
	blob, _ := json.Marshal(d.Result)
	if !strings.Contains(strings.ToLower(string(blob)), "circle") {
		t.Fatalf("describe of rhinoscriptsyntax.AddCircle lacks a circle summary: %s", blob)
	}
	// The Python call shape for a rhinoscriptsyntax function is its rs.* wrapper (PR4, "both call shapes").
	if pc, _ := d.Result["python_call"].(string); !strings.HasPrefix(pc, "rs.AddCircle(") {
		t.Fatalf("python_call for rhinoscriptsyntax.AddCircle = %q, want an rs.AddCircle(...) form: %+v", pc, d.Result)
	}

	// search surfaces at least one kind=rhinoscript member for a task phrase. Poll until the broker
	// index is ready (rhinoscript members ride the same dump_members corpus as RhinoCommon).
	deadline := time.Now().Add(120 * time.Second)
	var out searchFunctionsOut
	for {
		out, isErr = discCall[searchFunctionsOut](t, c, "search_functions", map[string]any{"query": "add a circle to the document"})
		if isErr {
			t.Fatalf("%+v", out.Error)
		}
		if out.Ranker == "semantic" || out.Ranker == "semantic-no-rerank" || out.Ranker == "lexical" {
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("broker index never became ready (ranker=%s)", out.Ranker)
		}
		time.Sleep(2 * time.Second)
	}
	var sawRhinoScript bool
	for _, m := range out.Results {
		// m.Kind is the MEMBER category (rhinoscript functions carry "function"), never the corpus
		// kind -- rhinoscript provenance rides the member-id prefix / synthetic namespace (#297, #2).
		if strings.HasPrefix(m.MemberID, "rhinoscript:") || m.Namespace == "rhinoscriptsyntax" {
			sawRhinoScript = true
		}
	}
	if !sawRhinoScript {
		t.Fatalf("no kind=rhinoscript member in the results for 'add a circle' (ranker=%s, %d results) -- the rhinoscript corpus is not reaching the broker index", out.Ranker, len(out.Results))
	}
	t.Logf("rhinoscript indexed: describe + search both surface rs functions (ranker=%s)", out.Ranker)
}

// TestDiscoveryTwoInstances is the two-live-instance acceptance pass for the
// dialer's multi-instance handling (issue #296). It NEEDS two Rhino instances
// connected -- Windows can run two, macOS is single-instance -- so it SKIPS with
// fewer. It verifies the dialer attached to BOTH (each is independently
// addressable, proving neither was lost to the F4 attach/detach race, whose
// ordering the dialer unit test pins) and the same-version instance-selection of
// an UNSCOPED discovery call (F2): a deterministic pick when the versions match,
// the ambiguous-instance-version refusal when they differ.
func TestDiscoveryTwoInstances(t *testing.T) {
	c := startServer(t)

	var insts []instance
	deadline := time.Now().Add(20 * time.Second)
	for {
		insts = listInstances(t, c).Instances
		if len(insts) >= 2 {
			break
		}
		if time.Now().After(deadline) {
			t.Skipf("need two connected Rhino instances for the #296 pass; have %d (Windows can run two, macOS cannot)", len(insts))
		}
		time.Sleep(500 * time.Millisecond)
	}

	// Both instances are independently addressable: a scoped describe on each
	// resolves and stamps that instance's own version -- proving the dialer
	// attached to both and each serves its own API.
	for _, in := range insts {
		d, isErr := discCall[describeFunctionOut](t, c, "describe_function",
			map[string]any{"instance_id": in.InstanceID, "member": "Rhino.Geometry.Sphere.Radius"})
		if isErr {
			t.Fatalf("scoped describe on instance %s failed: %+v", in.InstanceID, d.Error)
		}
		if d.Result == nil {
			t.Fatalf("scoped describe on instance %s returned no result", in.InstanceID)
		}
		if d.RhinoVersion != in.RhinoVersion {
			t.Errorf("scoped describe stamped rhino_version %q, want %q for instance %s", d.RhinoVersion, in.RhinoVersion, in.InstanceID)
		}
	}

	sameVersion := true
	for _, in := range insts[1:] {
		if in.RhinoVersion != insts[0].RhinoVersion {
			sameVersion = false
		}
	}

	// Unscoped discovery call -- F2 instance selection. The RANKER is intentionally
	// not asserted here: this is a single cold query, which legitimately uses the
	// plug-in's keyword fallback until the broker's per-instance semantic index
	// finishes building (two instances means two indexes building, so that window
	// is wider). What this case verifies is which INSTANCE an unscoped call selects,
	// not how it ranks -- the semantic path is covered by TestDiscoverySearchFunctionsFindsCircle.
	out, isErr := discCall[searchFunctionsOut](t, c, "search_functions", map[string]any{"query": "add a circle to the document"})
	if sameVersion {
		// A deterministic pick among same-version instances: the call SUCCEEDS (no
		// ambiguity) and stamps the shared version. Which same-version instance is
		// picked is arbitrary and documented -- the surface can differ by loaded
		// plug-in, which is why an agent passes instance_id when that matters.
		if isErr {
			t.Fatalf("unscoped search across %d same-version instances should succeed, got error: %+v", len(insts), out.Error)
		}
		if out.RhinoVersion != insts[0].RhinoVersion {
			t.Errorf("unscoped pick stamped version %q, want the shared %q", out.RhinoVersion, insts[0].RhinoVersion)
		}
		t.Logf("#296 two-instance: %d same-version instances; both addressable; unscoped call picked deterministically (ranker=%s)", len(insts), out.Ranker)
	} else {
		// Versions differ: an unscoped call must REFUSE rather than silently return
		// version-specific results.
		if !isErr || out.Error == nil || out.Error.Code != "ambiguous-instance-version" {
			t.Fatalf("unscoped search across differing versions must return ambiguous-instance-version, got isErr=%v error=%+v", isErr, out.Error)
		}
		t.Logf("#296 two-instance: instances span versions; both addressable; unscoped call correctly refused with ambiguous-instance-version")
	}
}
