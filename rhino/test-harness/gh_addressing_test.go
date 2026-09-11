//go:build harness

package harness_test

import (
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/rhino/test-harness/mcpclient"
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
				// PR2: addressing the definition binds it to the script's ghdoc global.
				assertGhdocBinds(t, c, inst.InstanceID, doc, g.GrasshopperDocumentID)
				return
			}
		}
		if time.Now().After(deadline) {
			t.Fatalf("no grasshopper_documents appeared for instance %s within the timeout", inst.InstanceID)
		}
		time.Sleep(1 * time.Second)
	}
}

// assertGhdocBinds runs a script with gh_document_id and checks the ghdoc global is the addressed
// GH_Document (not None); and that a bogus id is refused with grasshopper-document-not-found.
func assertGhdocBinds(t *testing.T, c *mcpclient.Client, instanceID, documentID, ghDocID string) {
	t.Helper()
	bound := callExecute(t, c, map[string]any{
		"instance_id": instanceID, "document_id": documentID, "gh_document_id": ghDocID, "language": "python",
		"script": "result = ('bound:%d' % ghdoc.ObjectCount) if ghdoc is not None else 'none'",
	}, 30*time.Second)
	t.Logf("ghdoc bind: status=%s return=%q", bound.Status, bound.ReturnValue)
	if bound.Error != nil {
		t.Logf("  error: code=%s msg=%s", bound.Error.Code, bound.Error.Message)
	}
	if bound.Status != "success" || !strings.HasPrefix(bound.ReturnValue, "bound:") {
		t.Errorf("gh_document_id should bind ghdoc to the definition, got status=%s return=%q", bound.Status, bound.ReturnValue)
	}

	// The C# path is the one PR #305's review flagged: GrasshopperDocument is typed object and the script
	// casts it to a Grasshopper type -- which only compiles if the Roslyn runner picked up Grasshopper.dll
	// after it was demand-loaded (the runner snapshots references at startup). This is the exact cast the
	// skill/schema instruct, so it must actually compile and run live.
	cs := callExecute(t, c, map[string]any{
		"instance_id": instanceID, "document_id": documentID, "gh_document_id": ghDocID, "language": "csharp",
		"script": "var gh = (Grasshopper.Kernel.GH_Document)GrasshopperDocument; return \"cs-bound:\" + gh.ObjectCount;",
	}, 30*time.Second)
	t.Logf("ghdoc C# cast: status=%s return=%q", cs.Status, cs.ReturnValue)
	if cs.Error != nil {
		t.Logf("  error: code=%s msg=%s", cs.Error.Code, cs.Error.Message)
	}
	if cs.Status != "success" || !strings.HasPrefix(cs.ReturnValue, "cs-bound:") {
		t.Errorf("C# cast to GH_Document should compile and run once Grasshopper is loaded, got status=%s return=%q", cs.Status, cs.ReturnValue)
	}

	bogus := callExecute(t, c, map[string]any{
		"instance_id": instanceID, "document_id": documentID, "gh_document_id": "gh-nope", "language": "python",
		"script": "result = 1",
	}, 30*time.Second)
	if bogus.Error == nil || bogus.Error.Code != "grasshopper-document-not-found" {
		t.Errorf("a bogus gh_document_id should fail grasshopper-document-not-found, got status=%s error=%+v", bogus.Status, bogus.Error)
	}
}

func docID(inst instance) string {
	if len(inst.Documents) > 0 {
		return inst.Documents[0].DocumentID
	}
	return ""
}
