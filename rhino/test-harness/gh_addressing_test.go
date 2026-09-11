//go:build harness

package harness_test

import (
	"strings"
	"testing"
	"time"
)

// TestGrasshopperDocumentsEnumerateLive verifies PRD §10 addressing end to end: with Grasshopper loaded
// and a definition present, list_instances reports it in grasshopper_documents with a gh- id, title,
// enabled flag and component count. It loads Grasshopper itself (demand-loaded) and adds a document via
// execute_script, so it needs no pre-opened .gh.
func TestGrasshopperDocumentsEnumerateLive(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)

	// A script needs an active document to run; skip (never fail) when the Rhino is at the template
	// chooser with none open, per the harness rule.
	doc := docID(inst)
	if doc == "" {
		t.Skip("no document open in the connected Rhino; open one so the load-Grasshopper script can run")
	}

	// Load the Grasshopper plug-in (its assemblies are demand-loaded) and add an empty definition to the
	// document server so there is something to enumerate.
	script := `
import Rhino, System
gh_id = System.Guid("b45a29b1-4343-4035-989e-044e8580d9cf")
loaded = Rhino.PlugIns.PlugIn.LoadPlugIn(gh_id)
import Grasshopper
server = Grasshopper.Instances.DocumentServer
doc = Grasshopper.Kernel.GH_Document()
try:
    server.AddDocument(doc, True)
except TypeError:
    server.AddDocument(doc)
result = "loaded={} count={} name={}".format(loaded, len(list(server)), doc.DisplayName)
`
	out := callExecute(t, c, map[string]any{
		"instance_id": inst.InstanceID, "document_id": doc, "language": "python", "script": script,
	}, 60*time.Second)
	t.Logf("load-grasshopper script: status=%s return=%q output=%q", out.Status, out.ReturnValue, out.Output)
	if out.Error != nil {
		t.Logf("  error: code=%s msg=%s", out.Error.Code, out.Error.Message)
	}
	for _, n := range out.Notices {
		t.Logf("  notice: code=%s msg=%s", n.Code, n.Message)
	}

	// list_instances should now show the definition (register fires on document events; poll a little).
	deadline := time.Now().Add(20 * time.Second)
	for {
		for _, i := range listInstances(t, c).Instances {
			if i.InstanceID != inst.InstanceID {
				continue
			}
			if len(i.GrasshopperDocuments) > 0 {
				g := i.GrasshopperDocuments[0]
				t.Logf("grasshopper_documents[0]: id=%s title=%q enabled=%v components=%d path=%q",
					g.GrasshopperDocumentID, g.Title, g.Enabled, g.ComponentCount, g.Path)
				if !strings.HasPrefix(g.GrasshopperDocumentID, "gh-") {
					t.Errorf("gh_document_id %q should start with gh-", g.GrasshopperDocumentID)
				}
				if g.Title == "" {
					t.Errorf("grasshopper document has an empty title: %+v", g)
				}
				if g.ComponentCount < 0 {
					t.Errorf("component_count is negative: %d", g.ComponentCount)
				}
				return
			}
		}
		if time.Now().After(deadline) {
			t.Fatalf("no grasshopper_documents appeared for instance %s within the timeout", inst.InstanceID)
		}
		time.Sleep(1 * time.Second)
	}
}

func docID(inst instance) string {
	if len(inst.Documents) > 0 {
		return inst.Documents[0].DocumentID
	}
	return ""
}
