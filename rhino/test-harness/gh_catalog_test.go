//go:build harness

package harness_test

import (
	"strings"
	"testing"
	"time"
)

// TestDiscoveryGrasshopperCatalogLive verifies the Grasshopper component catalog is indexed into discovery
// (PRD §09 kind=grasshopper): once Grasshopper is loaded, list_functions browses the Grasshopper namespace by
// category, search_functions finds a component, and describe_function returns its ports and a guid-based
// python_call. It loads Grasshopper itself, then polls because the catalog syncs on a background cadence.
func TestDiscoveryGrasshopperCatalogLive(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	doc := docID(inst)
	if doc == "" {
		t.Skip("no document open in the connected Rhino; open one so Grasshopper can be loaded")
	}

	// Load Grasshopper AND open a definition: the catalog sync is gated on a GH document being open (so the
	// ComponentServer is only read when GH is genuinely in use), and reads it on the UI thread.
	load := callExecute(t, c, map[string]any{
		"instance_id": inst.InstanceID, "document_id": doc, "language": "python",
		"script": `import Rhino, System
loaded = Rhino.PlugIns.PlugIn.LoadPlugIn(System.Guid("b45a29b1-4343-4035-989e-044e8580d9cf"))
import Grasshopper
server = Grasshopper.Instances.DocumentServer
if server.DocumentCount == 0:
    d = Grasshopper.Kernel.GH_Document()
    try:
        server.AddDocument(d, True)
    except TypeError:
        server.AddDocument(d)
result = "loaded=%s docs=%d" % (loaded, Grasshopper.Instances.DocumentServer.DocumentCount)`,
	}, 60*time.Second)
	t.Logf("load grasshopper: status=%s return=%q", load.Status, load.ReturnValue)
	if load.Status != "success" {
		t.Fatalf("could not load Grasshopper: %+v", load.Error)
	}

	// The catalog syncs on a slow background cadence after GH loads (and instantiates every proxy for ports),
	// so poll list_functions until the Grasshopper namespace appears.
	deadline := time.Now().Add(150 * time.Second)
	var types listFunctionsOut
	for {
		ns, _ := discCall[listFunctionsOut](t, c, "list_functions", map[string]any{})
		hasGH := false
		for _, n := range ns.Namespaces {
			if n.Namespace == "Grasshopper" {
				hasGH = true
			}
		}
		if hasGH {
			types, _ = discCall[listFunctionsOut](t, c, "list_functions", map[string]any{"namespace": "Grasshopper", "page_size": 5000})
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("the Grasshopper namespace never appeared in discovery within the timeout (%d namespaces)", len(ns.Namespaces))
		}
		time.Sleep(5 * time.Second)
	}

	t.Logf("Grasshopper categories: %.300q", types.Types)
	if !strings.Contains(types.Types, "Curve") {
		t.Errorf("expected a Curve category under Grasshopper, got %.300q", types.Types)
	}

	// The Curve category should carry the core Circle component.
	curve, _ := discCall[listFunctionsOut](t, c, "list_functions", map[string]any{"namespace": "Grasshopper", "type_name": "Curve", "page_size": 5000})
	if !strings.Contains(curve.Members, "Circle") {
		t.Errorf("expected a Circle component in Grasshopper.Curve, got %.300q", curve.Members)
	}

	// search_functions reflects the GH catalog once a server connects with Grasshopper already open (the
	// semsearch index is built at connect and not rebuilt mid-session, so the FIRST server, which attached
	// before GH synced, has a stale index; a fresh broker sees the catalog). Scope to the Grasshopper
	// namespace so a component surfaces above RhinoCommon's many "circle" members.
	c2 := startServer(t)
	waitForInstance(t, c2)
	var ghHit *discMember
	sdeadline := time.Now().Add(60 * time.Second)
	for {
		search, isErr := discCall[searchFunctionsOut](t, c2, "search_functions", map[string]any{"query": "circle", "namespace": "Grasshopper"})
		if isErr {
			t.Fatalf("search: %+v", search.Error)
		}
		for i := range search.Results {
			if search.Results[i].Namespace == "Grasshopper" {
				ghHit = &search.Results[i]
				break
			}
		}
		if ghHit != nil {
			t.Logf("search GH hit: %s kind=%s member_id=%s", ghHit.Name, ghHit.Kind, ghHit.MemberID)
			break
		}
		if time.Now().After(sdeadline) {
			t.Fatalf("search scoped to Grasshopper returned no Grasshopper-namespace result")
		}
		time.Sleep(3 * time.Second)
	}
	if ghHit.Kind != "GrasshopperComponent" {
		t.Errorf("a Grasshopper search hit should have kind GrasshopperComponent, got %q", ghHit.Kind)
	}

	// describe_function resolves a component by its dotted member_id and returns a guid-based placement call.
	desc, isErr := discCall[describeFunctionOut](t, c, "describe_function", map[string]any{"member": "Grasshopper.Curve.Circle"})
	if isErr {
		t.Fatalf("describe: %+v", desc.Error)
	}
	if desc.Result == nil {
		t.Fatalf("describe returned no result for Grasshopper.Curve.Circle")
	}
	pc, _ := desc.Result["python_call"].(string)
	if !strings.Contains(pc, "EmitObject") {
		t.Errorf("describe python_call should place the component by guid (EmitObject), got %q", pc)
	}
	t.Logf("describe Circle python_call: %s", pc)
}
