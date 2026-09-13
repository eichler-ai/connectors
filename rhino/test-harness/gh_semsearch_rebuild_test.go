//go:build harness

package harness_test

import (
	"testing"
	"time"
)

// TestSemsearchRebuildsWhenGrasshopperOpensLive verifies the broker rebuilds its search_functions index
// mid-session when the corpus changes: with a single broker connected before Grasshopper is in use, opening
// a Grasshopper definition (which syncs the component catalog, changing the corpus fingerprint) must make GH
// components searchable via the semantic index within a couple of poll cycles — no fresh connection needed.
func TestSemsearchRebuildsWhenGrasshopperOpensLive(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	doc := docID(inst)
	if doc == "" {
		t.Skip("no document open in the connected Rhino; open one")
	}

	// Open Grasshopper and a definition so the component catalog syncs (the corpus fingerprint changes).
	load := callExecute(t, c, map[string]any{
		"instance_id": inst.InstanceID, "document_id": doc, "language": "python",
		"script": `import Rhino, System
Rhino.PlugIns.PlugIn.LoadPlugIn(System.Guid("b45a29b1-4343-4035-989e-044e8580d9cf"))
import Grasshopper
import Grasshopper.Kernel as ghk
server = Grasshopper.Instances.DocumentServer
if server.DocumentCount == 0:
    d = ghk.GH_Document(); d.Enabled = True
    try: server.AddDocument(d, True)
    except TypeError: server.AddDocument(d)
result = "gh docs=%d" % Grasshopper.Instances.DocumentServer.DocumentCount`,
	}, 60*time.Second)
	if load.Status != "success" {
		t.Fatalf("could not open Grasshopper: %+v", load.Error)
	}

	// On the SAME broker, poll search_functions scoped to Grasshopper until a GrasshopperComponent surfaces
	// via the rebuilt semantic index (not the plug-in keyword fallback). Allow for the catalog sync cadence
	// plus the broker's poll/rebuild cycle.
	deadline := time.Now().Add(150 * time.Second)
	for {
		out, isErr := discCall[searchFunctionsOut](t, c, "search_functions", map[string]any{"query": "circle", "namespace": "Grasshopper"})
		if isErr {
			t.Fatalf("search: %+v", out.Error)
		}
		var ghHit *discMember
		for i := range out.Results {
			if out.Results[i].Kind == "GrasshopperComponent" {
				ghHit = &out.Results[i]
				break
			}
		}
		if ghHit != nil {
			t.Logf("GH component in the rebuilt index: %s kind=%s ranker=%s", ghHit.Name, ghHit.Kind, out.Ranker)
			if out.Ranker == "keyword-fallback" {
				t.Errorf("the GH hit came from the plug-in keyword fallback, not the rebuilt semantic index (ranker=%s)", out.Ranker)
			}
			return
		}
		if time.Now().After(deadline) {
			t.Fatalf("Grasshopper components never entered the search index after opening GH (ranker=%s, %d results)", out.Ranker, len(out.Results))
		}
		time.Sleep(5 * time.Second)
	}
}
