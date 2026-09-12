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
# The DocumentServer is process-global and accumulates a GH_Document per prior harness run; remove any
# so the enumerated grasshopper_documents[0] is deterministically the one this run creates.
for existing in list(server):
    server.RemoveDocument(existing)
doc = Grasshopper.Kernel.GH_Document()
slider = Grasshopper.Kernel.Special.GH_NumberSlider()
slider.NickName = "hslider"
slider.CreateAttributes()
doc.AddObject(slider, False)
crv = Grasshopper.Kernel.Parameters.Param_Curve()
crv.NickName = "crv"
crv.CreateAttributes()
doc.AddObject(crv, False)
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

	// PR3: a run that triggers a solve carries the grasshopper solve report.
	solve := callExecute(t, c, map[string]any{
		"instance_id": instanceID, "document_id": documentID, "gh_document_id": ghDocID, "language": "python",
		"script": "ghdoc.NewSolution(True)\nresult = 'solved'",
	}, 30*time.Second)
	solutions := 0
	if solve.Grasshopper != nil {
		solutions = len(solve.Grasshopper.Solutions)
	}
	t.Logf("gh solve: status=%s has_report=%v solutions=%d", solve.Status, solve.Grasshopper != nil, solutions)
	if solve.Status != "success" {
		t.Errorf("solve script failed: %+v", solve.Error)
	}
	if solutions == 0 {
		t.Errorf("a run that triggered a Grasshopper solve should carry a grasshopper report with solutions[], got %+v", solve.Grasshopper)
	}

	// PR4: Connector.Grasshopper drives the bound definition -- Find the slider, Set its value, Solve.
	drive := callExecute(t, c, map[string]any{
		"instance_id": instanceID, "document_id": documentID, "gh_document_id": ghDocID, "language": "python",
		"script": "c = connector.Grasshopper.Find('hslider')\nconnector.Grasshopper.Set('hslider', 7)\nconnector.Grasshopper.Solve(True)\nresult = ('found:%s' % c.Nickname) if c is not None else 'notfound'",
	}, 30*time.Second)
	t.Logf("connector.Grasshopper drive: status=%s return=%q", drive.Status, drive.ReturnValue)
	if drive.Error != nil {
		t.Logf("  error: code=%s msg=%s", drive.Error.Code, drive.Error.Message)
	}
	if drive.Status != "success" || drive.ReturnValue != "found:hslider" {
		t.Errorf("Connector.Grasshopper Find/Set/Solve should drive the slider, got status=%s return=%q", drive.Status, drive.ReturnValue)
	}

	// PR4: Connector.Grasshopper.Reference wires document geometry into an input parameter (the user's
	// priority). Add a real line to the document, reference it into the 'crv' Param_Curve by GUID, solve,
	// and confirm the parameter now carries one referenced item; then ClearReference empties it.
	ref := callExecute(t, c, map[string]any{
		"instance_id": instanceID, "document_id": documentID, "gh_document_id": ghDocID, "language": "python",
		"script": `import Rhino, Rhino.Geometry as rg
line = rg.Line(rg.Point3d(0,0,0), rg.Point3d(10,0,0))
oid = Rhino.RhinoDoc.ActiveDoc.Objects.AddLine(line)
connector.Grasshopper.Reference('crv', str(oid))
p = None
for o in ghdoc.Objects:
    if o.NickName == 'crv':
        p = o
        break
persist_ref = p.PersistentData.DataCount
goo = list(p.PersistentData.AllData(True))[0]
id_ok = str(goo.ReferenceID) == str(oid)
goo.LoadGeometry()
loaded = goo.IsValid
connector.Grasshopper.ClearReference('crv')
persist_clear = p.PersistentData.DataCount
result = 'persist_ref:%d id_ok:%s loaded:%s persist_clear:%d' % (persist_ref, id_ok, loaded, persist_clear)`,
	}, 30*time.Second)
	t.Logf("connector.Grasshopper reference: status=%s return=%q", ref.Status, ref.ReturnValue)
	if ref.Error != nil {
		t.Logf("  error: code=%s msg=%s", ref.Error.Code, ref.Error.Message)
	}
	if ref.Status != "success" || ref.ReturnValue != "persist_ref:1 id_ok:True loaded:True persist_clear:0" {
		t.Errorf("Connector.Grasshopper Reference/ClearReference should wire document geometry (right id, loads live) and unwire it, got status=%s return=%q", ref.Status, ref.ReturnValue)
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
