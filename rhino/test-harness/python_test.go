//go:build harness

package harness_test

import (
	"encoding/json"
	"fmt"
	"strings"
	"testing"
	"time"
)

// The Python host end to end (PRD §06, implementation-plan.md PR 4): real CPython 3 through
// Rhino.Runtime.Code, the four globals, result/stdout capture, rollback on a raise, the guard, and
// cooperative cancellation.

func TestPythonRunsCPython3_WithTheGlobals(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	out := python(t, c, inst, `import sys
print("impl", sys.implementation.name, sys.version_info[0])
result = {"impl": sys.implementation.name, "major": sys.version_info[0], "doc": type(doc).__name__, "ghdoc": ghdoc, "bridge": connector.BridgeVersion, "cancelled": cancel.IsRequested}
`, nil)
	if out.Status != "success" {
		t.Fatalf("%+v", out)
	}
	if !strings.Contains(out.Output, "impl cpython 3") {
		t.Fatalf("stdout: %q", out.Output)
	}
	for _, want := range []string{`"impl":"cpython"`, `"major":3`, `"doc":"RhinoDoc"`, `"ghdoc":null`, `"cancelled":false`} {
		if !strings.Contains(out.ReturnValue, want) {
			t.Fatalf("return_value %s lacks %s", out.ReturnValue, want)
		}
	}
}

func TestPythonCreatesObjects_AndReportsMutations(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	out := python(t, c, inst, `import Rhino
import rhinoscriptsyntax as rs
import scriptcontext as sc
assert sc.doc is doc, "scriptcontext.doc is not the routed document"
a = doc.Objects.AddSphere(Rhino.Geometry.Sphere(Rhino.Geometry.Point3d.Origin, 3))
b = rs.AddCircle((10, 0, 0), 2)
result = [str(a), str(b)]
`, map[string]any{"label": "python sphere and circle"})
	if out.Status != "success" {
		t.Fatalf("%+v", out)
	}
	var m struct {
		NetAdded     int `json:"net_added"`
		ByObjectType map[string]struct {
			Added int `json:"added"`
		} `json:"by_object_type"`
	}
	json.Unmarshal(out.Mutations, &m)
	if m.NetAdded != 2 || m.ByObjectType["Brep"].Added != 1 || m.ByObjectType["Curve"].Added != 1 {
		t.Fatalf("mutations = %s", out.Mutations)
	}
}

func TestPythonRaise_IsRolledBack_WithTheTraceback(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	out := python(t, c, inst, `import Rhino
doc.Objects.AddSphere(Rhino.Geometry.Sphere(Rhino.Geometry.Point3d.Origin, 1))
print("added")
raise RuntimeError("boom from python")
`, nil)
	if out.Status != "error" || out.Error == nil || out.Error.Code != "script-execution-failed" {
		t.Fatalf("%+v", out)
	}
	if !strings.Contains(out.Error.Message, "boom from python") {
		t.Fatalf("message: %s", out.Error.Message)
	}
	tb, _ := out.Error.Detail["traceback"].(string)
	if !strings.Contains(tb, "line 4") {
		t.Fatalf("traceback should point at line 4 of the script as sent: %q", tb)
	}
	if !strings.Contains(out.Output, "added") {
		t.Fatalf("stdout: %q", out.Output)
	}
	var rolled bool
	for _, n := range out.Notices {
		rolled = rolled || n.Code == "script-rolled-back"
	}
	if !rolled {
		t.Fatalf("notices: %+v", out.Notices)
	}
}

func TestPythonSyntaxError_IsACompileFailure(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	out := python(t, c, inst, "x = 1\ny = = 2\n", nil)
	if out.Status != "error" || out.Error == nil || out.Error.Code != "script-compilation-failed" {
		t.Fatalf("%+v", out)
	}
}

func TestPythonInteractiveGetter_IsRefused(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	out := python(t, c, inst, "import rhinoscriptsyntax as rs\nobj = rs.GetObject('pick one')\n", nil)
	if out.Status != "error" || out.Error == nil || out.Error.Code != "script-api-denied" {
		t.Fatalf("%+v", out)
	}
	if !strings.Contains(out.Error.Message, "rhinoscriptsyntax.GetObject") {
		t.Fatalf("message: %s", out.Error.Message)
	}
}

func TestPythonLifecycleGate_RefusesThenRunsWithTheFlag(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	script := `import Rhino
import os, tempfile
path = os.path.join(tempfile.gettempdir(), "mcp-python-gate.3dm")
opts = Rhino.FileIO.FileWriteOptions()
opts.SuppressDialogBoxes = True
opts.SuppressAllInput = True
ok = doc.Write3dmFile(path, opts)
result = {"ok": ok, "exists": os.path.exists(path)}
`
	out := python(t, c, inst, script, nil)
	if out.Status != "error" || out.Error == nil || out.Error.Code != "script-lifecycle-confirmation-required" {
		t.Fatalf("%+v", out)
	}
	out = python(t, c, inst, script, map[string]any{"confirm_lifecycle_actions": true})
	if out.Status != "success" || !strings.Contains(out.ReturnValue, `"exists":true`) {
		t.Fatalf("%+v", out)
	}
}

func TestPythonCancel_IsCooperative(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	tag := fmt.Sprintf("py5-%d", time.Now().UnixNano()%100000)
	// Adds an object, then spins until cancelled; the raise from cancel.Check() ends the run and
	// the executor reverts the point.
	out := python(t, c, inst, fmt.Sprintf(`import Rhino, time
attrs = Rhino.DocObjects.ObjectAttributes()
attrs.Name = "%s"
doc.Objects.AddPoint(Rhino.Geometry.Point3d.Origin, attrs)
while True:
    cancel.Check()
    time.sleep(0.05)
`, tag), map[string]any{"timeout_ms": 500})
	if out.Status != "running" && out.Status != "pending" {
		t.Fatalf("expected running, got %+v", out)
	}
	if _, err := c.CallTool("cancel_execution", map[string]any{"execution_id": out.ExecutionID}, 15*time.Second); err != nil {
		t.Fatal(err)
	}
	final := poll(t, c, out.ExecutionID, 10000)
	if final.Status != "cancelled" {
		t.Fatalf("expected cancelled, got %+v", final)
	}
	if has(objectNames(t, c, inst), tag) {
		t.Fatal("the cancelled run's object should have been undone")
	}
}
