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
	Error       *notice         `json:"error"`
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

func TestOneRunIsOneUndoEntry_NamedByTheLabel(t *testing.T) {
	// PRD §07 / §17.10: does an inner undo record name the entry? Read Rhino's command history.
	t.Skip("answered §17.10 (the entry is always MCPBridgeRun) while it ran against a single document; the CLI's _Undo acts on whichever document is active, so with several open it is not a valid oracle. Re-enable through the undo tool (PR 5), routed by document_id.")
	c := startServer(t)
	inst := waitForInstance(t, c)
	tag := fmt.Sprintf("h3-%d", time.Now().UnixNano()%100000)
	out := csharp(t, c, inst, fmt.Sprintf(`
for (int i = 0; i < 3; i++) Document.Objects.AddPoint(new Rhino.Geometry.Point3d(i, 0, 0), new Rhino.DocObjects.ObjectAttributes { Name = "%s-" + i });
return 3;`, tag), map[string]any{"label": "three points " + tag})
	if out.Status != "success" {
		t.Fatalf("%+v", out)
	}
	// Undo once through Rhino itself (not our tool: that is PR 5) and see whether all three go together.
	rc := "/Applications/Rhino 8.app/Contents/Resources/bin/rhinocode"
	if err := exec.Command(rc, "command", "_Undo").Run(); err != nil {
		t.Fatalf("rhinocode: %v", err)
	}
	time.Sleep(1500 * time.Millisecond)
	names := objectNames(t, c, inst)
	for i := 0; i < 3; i++ {
		if has(names, fmt.Sprintf("%s-%d", tag, i)) {
			t.Fatalf("one _Undo should revert the whole run; %s-%d survived", tag, i)
		}
	}
	// The entry's name was read from the history once (§17.10, closed: always "MCPBridgeRun"); the
	// Python read that did it is gone from the harness after two CPython crashes with rhinocode
	// scripts in play (caveats.md "Rhino crashed").
	// Redo so the objects come back for later cases' object-count sanity (best effort).
	exec.Command(rc, "command", "_Redo").Run()
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
while (true) { CancellationToken.ThrowIfCancellationRequested(); System.Threading.Thread.Sleep(50); }`, tag), map[string]any{"timeout_ms": 500})
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
	if out.Error == nil {
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
