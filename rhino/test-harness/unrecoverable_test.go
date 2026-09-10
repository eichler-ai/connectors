//go:build harness

package harness_test

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
	"time"
)

// TestNonCooperatingScriptGoesUnrecoverable (PRD §06): a script that ignores its cancellation
// token wedges Rhino's main thread for good; the plug-in keeps answering from its connection
// thread, resolves the run to `unrecoverable` after the grace period, and refuses further work
// with instance-unrecoverable. DESTRUCTIVE: it leaves Rhino wedged, so it runs only with
// MCP_HARNESS_DESTRUCTIVE=1 and restarts Rhino afterwards through the restart helper. Run it
// alone, last: go test -tags harness -run Unrecoverable ...
func TestNonCooperatingScriptGoesUnrecoverable(t *testing.T) {
	if os.Getenv("MCP_HARNESS_DESTRUCTIVE") != "1" {
		t.Skip("set MCP_HARNESS_DESTRUCTIVE=1 to run: this wedges Rhino and restarts it")
	}
	c := startServer(t)
	inst := waitForInstance(t, c)
	t.Cleanup(func() {
		// A wedged main thread cannot service the quit AppleEvent, so the helper's polite quit fails;
		// kill the process first (nothing is saved in a harness session), then let the helper relaunch.
		exec.Command("kill", "-9", fmt.Sprint(inst.PID)).Run()
		time.Sleep(2 * time.Second)
		helper, _ := filepath.Abs("../docs/spikes/phase-1a/rhino-restart.sh")
		out, err := exec.Command(helper).CombinedOutput()
		t.Logf("restart helper: %v\n%s", err, out)
	})

	out := csharp(t, c, inst, `while (true) { System.Threading.Thread.Sleep(100); }`, map[string]any{"timeout_ms": 500, "max_duration_ms": 60000})
	if out.Status != "running" && out.Status != "pending" {
		t.Fatalf("expected running, got %+v (error %+v)", out, out.Error)
	}
	if _, err := c.CallTool("cancel_execution", map[string]any{"execution_id": out.ExecutionID}, 15*time.Second); err != nil {
		t.Fatal(err)
	}
	// The grace period is seconds; poll until the record resolves.
	var final executionOut
	deadline := time.Now().Add(40 * time.Second)
	for time.Now().Before(deadline) {
		final = poll(t, c, out.ExecutionID, 2000)
		if final.Status == "unrecoverable" {
			break
		}
	}
	if final.Status != "unrecoverable" || final.Error == nil || final.Error.Code != "execution-cancellation-grace-expired" {
		t.Fatalf("after the grace period: %+v (error %+v)", final, final.Error)
	}
	t.Logf("resolved: %s -- %s", final.Error.Code, final.Error.Message)

	// Anything further on this instance is refused loudly, and list_instances says so.
	next := csharp(t, c, inst, `return 1;`, map[string]any{"timeout_ms": 500})
	if next.Status != "unrecoverable" {
		t.Fatalf("a new script on a wedged instance: %+v", next)
	}
	// The registry learns the state from the plug-in's next ping (PRD §05: the plug-in owns it).
	status := ""
	for deadline := time.Now().Add(20 * time.Second); time.Now().Before(deadline) && status != "unrecoverable"; {
		for _, i := range listInstances(t, c).Instances {
			if i.InstanceID == inst.InstanceID {
				status = i.Status
			}
		}
		time.Sleep(time.Second)
	}
	if status != "unrecoverable" {
		t.Fatalf("list_instances status = %s", status)
	}
}
