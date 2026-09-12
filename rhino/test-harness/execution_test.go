//go:build harness

package harness_test

import (
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/rhino/test-harness/mcpclient"
)

type executionOut struct {
	Status      string          `json:"status"`
	ExecutionID string          `json:"execution_id"`
	Output      string          `json:"output"`
	ReturnValue string          `json:"return_value"`
	Notices     []notice        `json:"notices"`
	Mutations   json.RawMessage `json:"mutations"`
	Grasshopper *ghReport       `json:"grasshopper"`
	Error       *notice         `json:"error"`
	LastRun     *lastRun        `json:"last_run"`
}

type ghReport struct {
	Solutions []struct {
		StartedAt  string  `json:"started_at"`
		DurationMs float64 `json:"duration_ms"`
		State      string  `json:"state"`
		Depth      int     `json:"depth"`
	} `json:"solutions"`
	Components []struct {
		GUID     string `json:"guid"`
		Nickname string `json:"nickname"`
		Type     string `json:"type"`
		Phase    string `json:"phase"`
		Messages []struct {
			Severity string `json:"severity"`
			Text     string `json:"text"`
		} `json:"messages"`
	} `json:"components"`
}

type lastRun struct {
	ExecutionID     string `json:"execution_id"`
	AgentClientID   string `json:"agent_client_id"`
	FinishedAt      string `json:"finished_at"`
	Status          string `json:"status"`
	Label           string `json:"label"`
	ChangedDocument bool   `json:"changed_document"`
}

type notice struct {
	Code     string         `json:"code"`
	Message  string         `json:"message"`
	Severity string         `json:"severity"`
	Detail   map[string]any `json:"detail"`
	Remedy   []string       `json:"remedy"`
}

// callExecute returns the decoded result whether or not the tool flagged IsError, since a refusal
// with a code IS the thing many cases assert on.
func callExecute(t *testing.T, c *mcpclient.Client, args map[string]any, timeout time.Duration) executionOut {
	t.Helper()
	raw, err := c.CallTool("execute_script", args, timeout)
	if err != nil {
		t.Fatalf("execute_script: %v", err)
	}
	var tr toolResult
	if err := json.Unmarshal(raw, &tr); err != nil {
		t.Fatalf("decode envelope: %v\n%s", err, raw)
	}
	var out executionOut
	src := tr.StructuredContent
	if len(src) == 0 && len(tr.Content) > 0 {
		src = json.RawMessage(tr.Content[0].Text)
	}
	if err := json.Unmarshal(src, &out); err != nil {
		t.Fatalf("decode result: %v\n%s", err, src)
	}
	return out
}

func csharp(t *testing.T, c *mcpclient.Client, inst instance, script string, extra map[string]any) executionOut {
	t.Helper()
	args := map[string]any{"instance_id": inst.InstanceID, "language": "csharp", "script": script}
	for k, v := range extra {
		args[k] = v
	}
	return callExecute(t, c, args, 60*time.Second)
}

func python(t *testing.T, c *mcpclient.Client, inst instance, script string, extra map[string]any) executionOut {
	t.Helper()
	args := map[string]any{"instance_id": inst.InstanceID, "language": "python", "script": script}
	for k, v := range extra {
		args[k] = v
	}
	return callExecute(t, c, args, 60*time.Second)
}

func poll(t *testing.T, c *mcpclient.Client, id string, timeoutMs int) executionOut {
	t.Helper()
	raw, err := c.CallTool("poll_execution", map[string]any{"execution_id": id, "timeout_ms": timeoutMs}, time.Duration(timeoutMs)*time.Millisecond+15*time.Second)
	if err != nil {
		t.Fatalf("poll_execution: %v", err)
	}
	var tr toolResult
	json.Unmarshal(raw, &tr)
	var out executionOut
	src := tr.StructuredContent
	if len(src) == 0 && len(tr.Content) > 0 {
		src = json.RawMessage(tr.Content[0].Text)
	}
	if err := json.Unmarshal(src, &out); err != nil {
		t.Fatalf("decode poll: %v\n%s", err, src)
	}
	return out
}

// objectNames reads the document's object names through a script, so assertions use NAMES, never counts.
func objectNames(t *testing.T, c *mcpclient.Client, inst instance) []string {
	t.Helper()
	out := csharp(t, c, inst, `var names = new System.Collections.Generic.List<string>(); foreach (var o in Document.Objects) names.Add(o.Name ?? ""); names.Sort(); return names;`, nil)
	if out.Status != "success" {
		t.Fatalf("reading names failed: %+v", out)
	}
	var names []string
	if err := json.Unmarshal([]byte(out.ReturnValue), &names); err != nil {
		t.Fatalf("names not JSON: %q", out.ReturnValue)
	}
	return names
}

func has(names []string, want string) bool {
	for _, n := range names {
		if n == want {
			return true
		}
	}
	return false
}

func TestCSharpCreatesObjects_AndReportsMutations(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	tag := fmt.Sprintf("h1-%d", time.Now().UnixNano()%100000)
	out := csharp(t, c, inst, fmt.Sprintf(`
var a = new Rhino.DocObjects.ObjectAttributes { Name = "%s-a" };
var b = new Rhino.DocObjects.ObjectAttributes { Name = "%s-b" };
Document.Objects.AddSphere(new Rhino.Geometry.Sphere(Rhino.Geometry.Point3d.Origin, 5), a);
Document.Objects.AddSphere(new Rhino.Geometry.Sphere(new Rhino.Geometry.Point3d(20, 0, 0), 5), b);
return Document.Objects.Count;`, tag, tag), map[string]any{"label": "harness spheres"})
	if out.Status != "success" {
		t.Fatalf("expected success, got %+v", out)
	}
	var m struct {
		NetAdded int `json:"net_added"`
	}
	json.Unmarshal(out.Mutations, &m)
	if m.NetAdded != 2 {
		t.Fatalf("mutations = %s", out.Mutations)
	}
	names := objectNames(t, c, inst)
	if !has(names, tag+"-a") || !has(names, tag+"-b") {
		t.Fatalf("objects missing: %v", names)
	}
}

func TestFailedScriptIsRolledBack(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	tag := fmt.Sprintf("h2-%d", time.Now().UnixNano()%100000)
	before := objectNames(t, c, inst)
	out := csharp(t, c, inst, fmt.Sprintf(`
Document.Objects.AddSphere(new Rhino.Geometry.Sphere(Rhino.Geometry.Point3d.Origin, 3), new Rhino.DocObjects.ObjectAttributes { Name = "%s" });
throw new System.InvalidOperationException("harness boom");`, tag), nil)
	if out.Status != "error" || out.Error == nil || out.Error.Code != "script-execution-failed" || !strings.Contains(out.Error.Message, "harness boom") {
		t.Fatalf("expected script-execution-failed with the message, got %+v", out)
	}
	var codes []string
	for _, n := range out.Notices {
		codes = append(codes, n.Code)
	}
	if !has(codes, "script-rolled-back") {
		t.Fatalf("expected a script-rolled-back notice, got %v", codes)
	}
	after := objectNames(t, c, inst)
	if has(after, tag) {
		t.Fatalf("the object survived the rollback: %v", after)
	}
	if len(after) != len(before) {
		t.Fatalf("rollback changed something else: before %v, after %v", before, after)
	}
}

func TestOneRunIsOneUndoEntry_UndoneAndRedoneByTheTools(t *testing.T) {
	// PRD §07: one run is one undo entry; the undo tool reverts exactly it without confirm (the
	// plug-in sees its own command on top), names the run, and redo restores it.
	c := startServer(t)
	inst := waitForInstance(t, c)
	tag := fmt.Sprintf("h3-%d", time.Now().UnixNano()%100000)
	out := csharp(t, c, inst, fmt.Sprintf(`
for (int i = 0; i < 3; i++) Document.Objects.AddPoint(new Rhino.Geometry.Point3d(i, 0, 0), new Rhino.DocObjects.ObjectAttributes { Name = "%s-" + i });
return 3;`, tag), map[string]any{"label": "three points " + tag})
	if out.Status != "success" {
		t.Fatalf("%+v", out)
	}
	undo := undoRedo(t, c, "undo", map[string]any{"instance_id": inst.InstanceID})
	if undo.Status != "success" {
		t.Fatalf("undo: %+v (error %+v)", undo, undo.Error)
	}
	if len(undo.Notices) != 1 || undo.Notices[0].Code != "undo-reverted-connector-work" || !strings.Contains(undo.Notices[0].Message, out.ExecutionID) || !strings.Contains(undo.Notices[0].Message, "three points") {
		t.Fatalf("undo notices: %+v", undo.Notices)
	}
	var m struct {
		NetDeleted int `json:"net_deleted"`
	}
	json.Unmarshal(undo.Mutations, &m)
	if m.NetDeleted != 3 {
		t.Fatalf("undo mutations = %s", undo.Mutations)
	}
	names := objectNames(t, c, inst)
	for i := 0; i < 3; i++ {
		if has(names, fmt.Sprintf("%s-%d", tag, i)) {
			t.Fatalf("one undo should revert the whole run; %s-%d survived", tag, i)
		}
	}
	// objectNames ran a script (a read-only run leaves no undo entry, so the redo stack is intact).
	redo := undoRedo(t, c, "redo", map[string]any{"instance_id": inst.InstanceID})
	if redo.Status != "success" {
		t.Fatalf("redo: %+v (error %+v)", redo, redo.Error)
	}
	names = objectNames(t, c, inst)
	for i := 0; i < 3; i++ {
		if !has(names, fmt.Sprintf("%s-%d", tag, i)) {
			t.Fatalf("redo should restore the run; %s-%d missing", tag, i)
		}
	}
}

func TestUndoAfterSomeoneElsesCommand_NeedsConfirm(t *testing.T) {
	// A person's change on top of the stack: the tool refuses without confirm, naming the last
	// command, and acts with confirm under a warning. The "person" is a Rhino command run from
	// outside the connector (rhinocode's command relay -- not a Python script, which is the shape
	// that crashed CPython; caveats.md). A change made INSIDE one of our runs, even through a nested
	// RunScript, is the connector's own and would not stage this.
	c := startServer(t)
	inst := waitForInstance(t, c)
	tag := fmt.Sprintf("h7-%d", time.Now().UnixNano()%100000)
	out := csharp(t, c, inst, fmt.Sprintf(`Document.Objects.AddPoint(Rhino.Geometry.Point3d.Origin, new Rhino.DocObjects.ObjectAttributes { Name = "%s" }); return 1;`, tag), nil)
	if out.Status != "success" {
		t.Fatalf("%+v", out)
	}
	rc := rhinocodePath()
	if rc == "" {
		t.Skip("no rhinocode CLI on this machine to stage a foreign command")
	}
	countBefore := len(objectNames(t, c, inst))
	if outb, err := exec.Command(rc, "command", "_Point 5,5,0").CombinedOutput(); err != nil {
		t.Fatalf("rhinocode: %v\n%s", err, outb)
	}
	// Wait for the person's point to exist rather than sleeping a fixed time (objectNames is a
	// read-only run and adds nothing itself).
	want := countBefore + 1
	for deadline := time.Now().Add(10 * time.Second); len(objectNames(t, c, inst)) < want && time.Now().Before(deadline); {
		time.Sleep(200 * time.Millisecond)
	}
	refused := undoRedo(t, c, "undo", map[string]any{"instance_id": inst.InstanceID})
	if refused.Status == "success" {
		t.Fatalf("an undo after a foreign change must need confirm: %+v", refused)
	}
	if refused.Error == nil || refused.Error.Code != "undo-confirmation-required" {
		t.Fatalf("refused: %+v", refused)
	}
	t.Logf("refusal: %s", refused.Error.Message)
	forced := undoRedo(t, c, "undo", map[string]any{"instance_id": inst.InstanceID, "confirm": true})
	if forced.Status != "success" || len(forced.Notices) != 1 || forced.Notices[0].Code != "undo-reverted-other-work" {
		t.Fatalf("forced: %+v (error %+v)", forced, forced.Error)
	}
	// Exactly the person's point is gone; ours (below it) is still there.
	var m struct {
		NetDeleted int `json:"net_deleted"`
	}
	json.Unmarshal(forced.Mutations, &m)
	if m.NetDeleted != 1 {
		t.Fatalf("confirmed undo mutations = %s", forced.Mutations)
	}
	if !has(objectNames(t, c, inst), tag) {
		t.Fatal("the confirmed undo reverted more than the top entry")
	}
}

func TestOmittedDocumentIdIsActive(t *testing.T) {
	// PRD §05: a script with no document_id addresses the active document, on both platforms (on
	// Windows there is only ever one, which is the active one; on the Mac there may be several).
	// csharp() deliberately omits document_id, so the run's last_run must land on the document that
	// list_instances marks active.
	c := startServer(t)
	inst := waitForInstance(t, c)
	waitForIdle(t, c, inst.InstanceID)

	var activeBefore string
	for _, d := range inst.Documents {
		if d.Active {
			activeBefore = d.DocumentID
		}
	}
	if activeBefore == "" {
		t.Fatalf("no active document reported: %+v", inst.Documents)
	}

	out := csharp(t, c, inst, `Document.Objects.AddPoint(new Rhino.Geometry.Point3d(3, 3, 3)); return 1;`, map[string]any{"label": "omitted-doc probe"})
	if out.Status != "success" {
		t.Fatalf("omitted-document_id run failed: %+v (error %+v)", out, out.Error)
	}

	var carrier document
	var found bool
	for _, i := range listInstances(t, c).Instances {
		if i.InstanceID != inst.InstanceID {
			continue
		}
		for _, d := range i.Documents {
			if d.LastRun != nil && d.LastRun.ExecutionID == out.ExecutionID {
				carrier, found = d, true
			}
		}
	}
	if !found {
		t.Fatalf("no document carries the run's last_run %s", out.ExecutionID)
	}
	if !carrier.Active || carrier.DocumentID != activeBefore {
		t.Fatalf("omitted document_id did not target the active document: landed on %q (active=%v), expected active %q", carrier.DocumentID, carrier.Active, activeBefore)
	}
	t.Logf("omitted document_id addressed the active document %s (%s)", carrier.DocumentID, carrier.Title)
}

func TestLastRun_IsReportedPerDocument_AndOnTheNextResult(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	first := csharp(t, c, inst, `Document.Objects.AddPoint(new Rhino.Geometry.Point3d(9, 9, 9)); return 1;`, map[string]any{"label": "last-run probe"})
	if first.Status != "success" {
		t.Fatalf("%+v", first)
	}
	// list_instances: the active document carries last_run for this execution.
	var found bool
	for _, i := range listInstances(t, c).Instances {
		if i.InstanceID != inst.InstanceID {
			continue
		}
		for _, d := range i.Documents {
			if d.LastRun != nil && d.LastRun.ExecutionID == first.ExecutionID {
				found = true
				if d.LastRun.Label != "last-run probe" || !d.LastRun.ChangedDocument || d.LastRun.AgentClientID == "" {
					t.Fatalf("last_run = %+v", d.LastRun)
				}
			}
		}
	}
	if !found {
		t.Fatalf("no document carries last_run %s", first.ExecutionID)
	}
	second := csharp(t, c, inst, `return 2;`, nil)
	if second.LastRun == nil || second.LastRun.ExecutionID != first.ExecutionID {
		t.Fatalf("the second result should carry the first run as last_run: %+v", second.LastRun)
	}
}

func undoRedo(t *testing.T, c *mcpclient.Client, direction string, args map[string]any) executionOut {
	t.Helper()
	raw, err := c.CallTool(direction, args, 30*time.Second)
	if err != nil {
		t.Fatalf("%s: %v", direction, err)
	}
	var tr toolResult
	json.Unmarshal(raw, &tr)
	var out executionOut
	src := tr.StructuredContent
	if len(src) == 0 && len(tr.Content) > 0 {
		src = json.RawMessage(tr.Content[0].Text)
	}
	if err := json.Unmarshal(src, &out); err != nil {
		t.Fatalf("decode %s: %v\n%s", direction, err, src)
	}
	return out
}

func TestInteractiveGetterIsRefused(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	out := csharp(t, c, inst, `Rhino.Geometry.Point3d p; Rhino.Input.RhinoGet.GetPoint("pick", false, out p); return p;`, nil)
	if out.Status != "error" || out.Error == nil || out.Error.Code != "script-api-denied" {
		t.Fatalf("expected script-api-denied, got %+v", out)
	}
	if !strings.Contains(out.Error.Message, "RhinoGet.GetPoint") {
		t.Fatalf("message should name the member: %s", out.Error.Message)
	}
}

func TestLifecycleGate_RefusesThenRunsWithTheFlag(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	// Write a copy to a temp path: an effect outside the document, gated. Write3dmFile with dialogs
	// suppressed, NOT Document.Export: Export("x.obj") opens the OBJ options prompt inside the run
	// and blocks Rhino's main thread until a person answers -- found live, the PRD §08 case in the flesh.
	path := strings.ReplaceAll(t.TempDir()+"/h.3dm", "\\", "/")
	script := fmt.Sprintf(`var o = new Rhino.FileIO.FileWriteOptions { SuppressDialogBoxes = true, SuppressAllInput = true }; return Document.Write3dmFile("%s", o);`, path)
	refused := csharp(t, c, inst, script, nil)
	if refused.Error == nil || refused.Error.Code != "script-lifecycle-confirmation-required" {
		t.Fatalf("expected the gate, got %+v", refused)
	}
	allowed := csharp(t, c, inst, script, map[string]any{"confirm_lifecycle_actions": true})
	if allowed.Status != "success" {
		t.Fatalf("with the flag: %+v", allowed)
	}
	if _, err := os.Stat(path); err != nil {
		t.Fatalf("export did not write %s: %v", path, err)
	}
}

func TestCancelResolvesCancelled_AndUndoes(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	tag := fmt.Sprintf("h5-%d", time.Now().UnixNano()%100000)
	out := csharp(t, c, inst, fmt.Sprintf(`
Document.Objects.AddPoint(Rhino.Geometry.Point3d.Origin, new Rhino.DocObjects.ObjectAttributes { Name = "%s" });
while (true) { CancellationToken.ThrowIfCancellationRequested(); System.Threading.Thread.Sleep(50); }`, tag), map[string]any{"timeout_ms": 500, "max_duration_ms": 30000})
	if out.Status != "running" && out.Status != "pending" {
		t.Fatalf("expected running, got %+v (error: %+v)", out, out.Error)
	}
	raw, err := c.CallTool("cancel_execution", map[string]any{"execution_id": out.ExecutionID}, 15*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	final := poll(t, c, out.ExecutionID, 10000)
	if final.Status != "cancelled" {
		t.Fatalf("expected cancelled, got %+v (cancel raw: %s)", final, raw)
	}
	if has(objectNames(t, c, inst), tag) {
		t.Fatal("the cancelled run's object should have been undone")
	}
}

func TestBusyWhileRunning_ThenPollFinishes(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	long := csharp(t, c, inst, `var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < 3000) { CancellationToken.ThrowIfCancellationRequested(); System.Threading.Thread.Sleep(50); } return "done";`, map[string]any{"timeout_ms": 300})
	if long.Status != "running" && long.Status != "pending" {
		t.Fatalf("expected running, got %+v", long)
	}
	second := csharp(t, c, inst, `return 1;`, map[string]any{"timeout_ms": 300})
	if second.Status != "busy" || second.ExecutionID != long.ExecutionID {
		t.Fatalf("expected busy pointing at %s, got %+v", long.ExecutionID, second)
	}
	final := poll(t, c, long.ExecutionID, 10000)
	if final.Status != "success" || final.ReturnValue != "done" {
		t.Fatalf("poll: %+v", final)
	}
}

func TestDocumentNotFound_ListsCandidates(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	out := csharp(t, c, inst, `return 1;`, map[string]any{"document_id": "doc-000000000000"})
	if out.Error == nil || out.Error.Code != "document-not-found" {
		t.Fatalf("%+v", out)
	}
	if _, ok := out.Error.Detail["open_documents"]; !ok {
		t.Fatalf("no candidates in %v", out.Error.Detail)
	}
}

func TestUnknownLanguageIsRefused_Loudly(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	// The server's schema refuses anything but csharp/python before the bridge sees it.
	out := callExecute(t, c, map[string]any{"instance_id": inst.InstanceID, "language": "ruby", "script": "puts 1"}, 30*time.Second)
	if out.Error == nil || out.Error.Code != "invalid-param" || !strings.Contains(out.Error.Message, "language") {
		t.Fatalf("%+v", out)
	}
}

func TestNonObjectChangeIsRolledBackToo(t *testing.T) {
	// review of #282: a layer added by a run that then throws must be reverted even though no
	// object event fired and the mutation report is empty.
	c := startServer(t)
	inst := waitForInstance(t, c)
	layer := fmt.Sprintf("h6-%d", time.Now().UnixNano()%100000)
	out := csharp(t, c, inst, fmt.Sprintf(`Document.Layers.Add("%s", System.Drawing.Color.Red); throw new System.Exception("after layer");`, layer), nil)
	if out.Status != "error" {
		t.Fatalf("%+v", out)
	}
	var codes []string
	for _, n := range out.Notices {
		codes = append(codes, n.Code)
	}
	if !has(codes, "script-rolled-back") {
		t.Fatalf("expected script-rolled-back, got %v", codes)
	}
	check := csharp(t, c, inst, fmt.Sprintf(`return Document.Layers.FindName("%s") == null ? "gone" : "present";`, layer), nil)
	if check.ReturnValue != "gone" {
		t.Fatalf("layer should have been undone: %+v", check)
	}
}
