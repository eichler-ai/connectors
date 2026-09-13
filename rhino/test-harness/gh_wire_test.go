//go:build harness

package harness_test

import (
	"strings"
	"testing"
	"time"
)

// TestGrasshopperConnectWiresComponentsLive verifies Connector.Grasshopper.Connect / Disconnect /
// ClearSources against real Grasshopper: it builds a fixture definition (a Number Slider, a Panel, and an
// Addition component), wires them with the connector, and confirms a value set on the slider flows through
// the wire to the panel — and stops flowing when disconnected. Also exercises component-input-by-name
// resolution and the clear-port error paths.
func TestGrasshopperConnectWiresComponentsLive(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	doc := docID(inst)
	if doc == "" {
		t.Skip("no document open in the connected Rhino; open one so Grasshopper can be loaded")
	}

	// Build a fixture definition with UNWIRED objects: a slider "S", a panel "P", and an Addition
	// component "ADD" (two inputs A/B — to exercise a component with more than one port). Wiring is left to
	// the connector under test.
	setup := callExecute(t, c, map[string]any{
		"instance_id": inst.InstanceID, "document_id": doc, "language": "python",
		"script": `import Rhino, System
loaded = Rhino.PlugIns.PlugIn.LoadPlugIn(System.Guid("b45a29b1-4343-4035-989e-044e8580d9cf"))
import Grasshopper
import Grasshopper.Kernel as ghk
import Grasshopper.Kernel.Special as ghs
server = Grasshopper.Instances.DocumentServer
doc = ghk.GH_Document()
sl = ghs.GH_NumberSlider()
sl.NickName = "S"; sl.CreateAttributes(); doc.AddObject(sl, False)
pn = ghs.GH_Panel()
pn.NickName = "P"; pn.CreateAttributes(); doc.AddObject(pn, False)
def guid_for(name):
    for p in Grasshopper.Instances.ComponentServer.ObjectProxies:
        try:
            if p.Desc.Name == name and not p.Obsolete: return p.Guid
        except: pass
    return None
ag = guid_for("Addition")
added = "no"
if ag is not None:
    add = Grasshopper.Instances.ComponentServer.EmitObject(ag)
    add.NickName = "ADD"; add.CreateAttributes(); doc.AddObject(add, False)
    added = "yes inputs=%d" % add.Params.Input.Count
doc.Enabled = True
try:
    server.AddDocument(doc, True)
except TypeError:
    server.AddDocument(doc)
result = "ok objects=%d addition=%s" % (doc.ObjectCount, added)`,
	}, 60*time.Second)
	t.Logf("setup: status=%s return=%q err=%+v", setup.Status, setup.ReturnValue, setup.Error)
	if setup.Status != "success" {
		t.Fatalf("could not build the fixture definition: %+v", setup.Error)
	}

	// Find the gh_document_id the bridge assigned to the new definition.
	ghID := ""
	deadline := time.Now().Add(20 * time.Second)
	for ghID == "" && time.Now().Before(deadline) {
		for _, i := range listInstances(t, c).Instances {
			if i.InstanceID == inst.InstanceID && len(i.GrasshopperDocuments) > 0 {
				ghID = i.GrasshopperDocuments[len(i.GrasshopperDocuments)-1].GrasshopperDocumentID
			}
		}
		if ghID == "" {
			time.Sleep(1 * time.Second)
		}
	}
	if ghID == "" {
		t.Fatalf("no gh_documents appeared for instance %s", inst.InstanceID)
	}

	// The core proof: wire slider -> panel with Connect, and a value set on the slider must appear on the
	// panel after a solve (the wire carried it); Disconnect must stop it. Free-param ports use "".
	wire := callExecute(t, c, map[string]any{
		"instance_id": inst.InstanceID, "document_id": doc, "gh_document_id": ghID, "language": "python",
		"script": `def val(name):
    v = connector.Grasshopper.Get(name)
    return "" if v.Count == 0 else str(v.Items[0].Value)
before = val("P")
connector.Grasshopper.Connect("S", "", "P", "")
connector.Grasshopper.Set("S", 0.5)
connector.Grasshopper.Solve(True)
sval = val("S")
after = val("P")
connector.Grasshopper.Disconnect("S", "", "P", "")
connector.Grasshopper.Solve(True)
disc = val("P")
result = "before=[%s] slider=[%s] after=[%s] disc=[%s]" % (before, sval, after, disc)`,
	}, 30*time.Second)
	t.Logf("wire slider->panel: status=%s return=%q err=%+v", wire.Status, wire.ReturnValue, wire.Error)
	if wire.Status != "success" {
		t.Fatalf("wiring run failed: %+v", wire.Error)
	}
	// The proof the wire carried the value, stated in terms of the slider's value (0.5), which is robust
	// whether an unwired panel reads as empty or as its own placeholder text: the value is ABSENT before
	// wiring, PRESENT once wired + solved, and ABSENT again after Disconnect.
	seg := func(name string) string {
		i := strings.Index(wire.ReturnValue, name+"=[")
		if i < 0 {
			return ""
		}
		rest := wire.ReturnValue[i+len(name)+2:]
		return rest[:strings.IndexByte(rest, ']')]
	}
	if strings.Contains(seg("before"), "0.5") {
		t.Errorf("the panel should not carry 0.5 before it is wired, got %q", wire.ReturnValue)
	}
	if !strings.Contains(seg("after"), "0.5") {
		t.Errorf("the slider's value should flow through the wire to the panel, got %q", wire.ReturnValue)
	}
	if strings.Contains(seg("disc"), "0.5") {
		t.Errorf("Disconnect should stop the slider's value reaching the panel, got %q", wire.ReturnValue)
	}

	// Component-input-by-name resolution + the error paths.
	comp := callExecute(t, c, map[string]any{
		"instance_id": inst.InstanceID, "document_id": doc, "gh_document_id": ghID, "language": "python",
		"script": `# Wire slider into the Addition component's "A" input BY NAME, then read back via the raw API
# that the input now has one source (the slider).
connector.Grasshopper.Connect("S", "", "ADD", "A")
add = None
for o in ghdoc.Objects:
    if o.NickName == "ADD": add = o
srcA = add.Params.Input[0].SourceCount
# An empty port on a multi-input component is ambiguous -> a clear error, not a silent pick.
try:
    connector.Grasshopper.Connect("S", "", "ADD", "")
    amb = "no-error"
except Exception:
    amb = "err"
# A missing target object errors too.
try:
    connector.Grasshopper.Connect("S", "", "NOPE", "")
    bad = "no-error"
except Exception:
    bad = "err"
# ClearSources removes the wire we just made.
connector.Grasshopper.ClearSources("ADD", "A")
srcAfter = add.Params.Input[0].SourceCount
result = "srcA=%d ambiguous=%s badtarget=%s cleared=%d" % (srcA, amb, bad, srcAfter)`,
	}, 30*time.Second)
	t.Logf("component ports: status=%s return=%q err=%+v", comp.Status, comp.ReturnValue, comp.Error)
	if comp.Status != "success" {
		t.Fatalf("component-port run failed: %+v", comp.Error)
	}
	for _, want := range []string{"srcA=1", "ambiguous=err", "badtarget=err", "cleared=0"} {
		if !strings.Contains(comp.ReturnValue, want) {
			t.Errorf("expected %q in the component-port result, got %q", want, comp.ReturnValue)
		}
	}
}
