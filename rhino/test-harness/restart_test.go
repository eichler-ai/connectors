//go:build harness

package harness_test

import (
	"testing"
	"time"
)

type restartRhinoOut struct {
	Status          string   `json:"status"`
	ReopenDocuments []string `json:"reopen_documents"`
	Unsaved         []string `json:"unsaved"`
	Notice          string   `json:"notice"`
}

// TestRestartRhinoPreviewLive verifies restart_rhino's read-only preview path against a live Rhino: it
// reports what would be quit and reopened without restarting anything (no confirm_lifecycle_actions). This
// exercises the new restart_snapshot bridge method end to end. It deliberately never confirms, so the
// running Rhino is not touched.
func TestRestartRhinoPreviewLive(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)

	raw, err := c.CallTool("restart_rhino", map[string]any{"instance_id": inst.InstanceID}, 30*time.Second)
	if err != nil {
		t.Fatalf("restart_rhino: %v", err)
	}
	out := decodeToolResult[restartRhinoOut](t, raw)
	t.Logf("restart preview: status=%s reopen=%v unsaved=%v notice=%q", out.Status, out.ReopenDocuments, out.Unsaved, out.Notice)
	if out.Status != "preview" {
		t.Errorf("restart_rhino without confirm should preview, got status=%q", out.Status)
	}
	if out.Notice == "" {
		t.Errorf("preview should carry a notice describing the restart")
	}
}
