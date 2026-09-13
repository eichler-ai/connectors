//go:build harness

package harness_test

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// TestGrasshopperUndoAndSaveLive verifies, against real Grasshopper on Rhino 8.35:
//   - the connector's Grasshopper edits in ONE run collapse into ONE undo entry the user can revert, and a
//     single Undo reverts BOTH a Set and a wire (per-run grouping);
//   - Connector.Grasshopper.Save writes a .gh, sets the document's FilePath, and is refused without
//     confirm_lifecycle_actions.
func TestGrasshopperUndoAndSaveLive(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	doc := docID(inst)
	if doc == "" {
		t.Skip("no document open in the connected Rhino; open one so Grasshopper can be loaded")
	}

	setup := callExecute(t, c, map[string]any{
		"instance_id": inst.InstanceID, "document_id": doc, "language": "python",
		"script": `import Rhino, System
Rhino.PlugIns.PlugIn.LoadPlugIn(System.Guid("b45a29b1-4343-4035-989e-044e8580d9cf"))
import Grasshopper
import Grasshopper.Kernel as ghk
import Grasshopper.Kernel.Special as ghs
server = Grasshopper.Instances.DocumentServer
d = ghk.GH_Document()
sl = ghs.GH_NumberSlider(); sl.NickName = "S"; sl.CreateAttributes(); d.AddObject(sl, False)
pn = ghs.GH_Panel(); pn.NickName = "P"; pn.CreateAttributes(); d.AddObject(pn, False)
d.Enabled = True
try: server.AddDocument(d, True)
except TypeError: server.AddDocument(d)
result = "ok"`,
	}, 60*time.Second)
	if setup.Status != "success" {
		t.Fatalf("fixture setup failed: %+v", setup.Error)
	}
	ghID := ""
	deadline := time.Now().Add(15 * time.Second)
	for ghID == "" && time.Now().Before(deadline) {
		for _, i := range listInstances(t, c).Instances {
			if i.InstanceID == inst.InstanceID && len(i.GrasshopperDocuments) > 0 {
				ghID = i.GrasshopperDocuments[len(i.GrasshopperDocuments)-1].GrasshopperDocumentID
			}
		}
		if ghID == "" {
			time.Sleep(time.Second)
		}
	}
	if ghID == "" {
		t.Fatalf("no gh_documents appeared for instance %s", inst.InstanceID)
	}

	// Run A: two edits (a Set and a wire) in one run. The grouped undo record is pushed when the run ends.
	runA := callExecute(t, c, map[string]any{
		"instance_id": inst.InstanceID, "document_id": doc, "gh_document_id": ghID, "language": "python",
		"script": `start = ghdoc.UndoServer.UndoCount
connector.Grasshopper.Set("S", 0.9)
connector.Grasshopper.Connect("S", "", "P", "")
result = "start=%d" % start`,
	}, 30*time.Second)
	t.Logf("run A (edit): status=%s return=%q err=%+v", runA.Status, runA.ReturnValue, runA.Error)
	if runA.Status != "success" {
		t.Fatalf("edit run failed: %+v", runA.Error)
	}

	// Run B: the two edits are now ONE undo entry; a single Undo reverts BOTH.
	runB := callExecute(t, c, map[string]any{
		"instance_id": inst.InstanceID, "document_id": doc, "gh_document_id": ghID, "language": "python",
		"script": `sl = None; pn = None
for o in ghdoc.Objects:
    if o.NickName == "S": sl = o
    if o.NickName == "P": pn = o
end = ghdoc.UndoServer.UndoCount
wired_before = pn.SourceCount
val_before = str(sl.CurrentValue)
ghdoc.Undo()                      # what the user's Ctrl+Z does in the GH editor
wired_after = pn.SourceCount
val_after = str(sl.CurrentValue)
result = "end=%d wired_before=%d val_before=%s wired_after=%d val_after=%s" % (end, wired_before, val_before, wired_after, val_after)`,
	}, 30*time.Second)
	t.Logf("run B (undo): status=%s return=%q err=%+v", runB.Status, runB.ReturnValue, runB.Error)
	if runB.Status != "success" {
		t.Fatalf("undo run failed: %+v", runB.Error)
	}
	// One run added exactly one undo entry.
	start := afterEq(runA.ReturnValue, "start=")
	end := afterEq(runB.ReturnValue, "end=")
	if start == "" || end == "" || atoi(end) != atoi(start)+1 {
		t.Errorf("one run should add exactly one grouped GH undo entry: start=%s end=%s", start, end)
	}
	// A single Undo reverted BOTH the wire (1->0) and the Set (0.9 -> something else).
	if !strings.Contains(runB.ReturnValue, "wired_before=1") || !strings.Contains(runB.ReturnValue, "wired_after=0") {
		t.Errorf("one Undo should unwire the panel, got %q", runB.ReturnValue)
	}
	if !strings.Contains(runB.ReturnValue, "val_before=0.9") || strings.Contains(afterEq(runB.ReturnValue, "val_after="), "0.9") {
		t.Errorf("one Undo should revert the slider value away from 0.9, got %q", runB.ReturnValue)
	}

	// Save WITHOUT confirmation is refused by the lifecycle gate. Do this BEFORE the confirmed save:
	// saving sets FilePath, which changes the definition's gh_document_id (identity is path-derived once
	// saved), so a later call with this id would fail as not-found for the wrong reason.
	refusePath := filepath.Join(t.TempDir(), "should_not_exist.gh")
	refused := callExecute(t, c, map[string]any{
		"instance_id": inst.InstanceID, "document_id": doc, "gh_document_id": ghID, "language": "python",
		"script": `connector.Grasshopper.Save(r"` + refusePath + `")` + "\nresult = 'saved'",
	}, 30*time.Second)
	t.Logf("save (unconfirmed): status=%s err=%+v", refused.Status, refused.Error)
	if refused.Status == "success" || refused.Error == nil || !strings.Contains(refused.Error.Code, "lifecycle") {
		t.Errorf("an unconfirmed GH save should be refused with a lifecycle-confirmation error, got status=%s err=%+v", refused.Status, refused.Error)
	}
	if _, err := os.Stat(refusePath); err == nil {
		t.Errorf("a refused save must not write a file")
	}

	// Save WITH confirm_lifecycle_actions writes the file and sets FilePath.
	savePath := filepath.Join(t.TempDir(), "mcp_saved_def.gh")
	save := callExecute(t, c, map[string]any{
		"instance_id": inst.InstanceID, "document_id": doc, "gh_document_id": ghID, "language": "python",
		"confirm_lifecycle_actions": true,
		"script":                    `connector.Grasshopper.Save(r"` + savePath + `")` + "\nresult = ghdoc.FilePath",
	}, 30*time.Second)
	t.Logf("save (confirmed): status=%s return=%q err=%+v", save.Status, save.ReturnValue, save.Error)
	if save.Status != "success" {
		t.Fatalf("confirmed save failed: %+v", save.Error)
	}
	if _, err := os.Stat(savePath); err != nil {
		t.Errorf("Save should have written the file at %s: %v", savePath, err)
	}
	if !strings.Contains(save.ReturnValue, "mcp_saved_def.gh") {
		t.Errorf("Save should set the document FilePath, got %q", save.ReturnValue)
	}
}

func afterEq(s, key string) string {
	i := strings.Index(s, key)
	if i < 0 {
		return ""
	}
	rest := s[i+len(key):]
	if j := strings.IndexByte(rest, ' '); j >= 0 {
		return rest[:j]
	}
	return rest
}

func atoi(s string) int {
	n := 0
	for _, r := range s {
		if r < '0' || r > '9' {
			return -1
		}
		n = n*10 + int(r-'0')
	}
	return n
}
