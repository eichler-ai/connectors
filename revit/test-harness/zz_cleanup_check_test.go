//go:build harness

package harness_test

import (
	"strings"
	"testing"
	"time"
)

// TestZZDocumentCleanupRoundTrip verifies the cleanup discipline itself,
// end to end: create a document, watch it APPEAR in list_instances (the
// issue #30 live snapshot push), close it via the shared
// closeDocumentByTitle helper, and watch it DISAPPEAR again. The zz_ file
// name places it last in the suite's source order on purpose -- by the time
// it runs, every earlier case's t.Cleanup has already fired, so a helper
// regression that silently stopped closing documents shows up here as this
// test's own create/close round trip failing.
//
// Deliberately RELATIVE to a baseline count taken at test start, not an
// absolute "only the fixture remains" assertion: the shared live session may
// carry unsaved documents from BEFORE this suite ran (older sessions, manual
// work), and failing on pre-existing state would make the check flaky in
// exactly the environments it's meant to protect. The relative round trip
// pins everything this suite is responsible for.
func TestZZDocumentCleanupRoundTrip(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	countDocuments := func() int {
		raw, err := c.CallTool("list_instances", map[string]any{}, 10*time.Second)
		if err != nil {
			t.Fatalf("list_instances: %v", err)
		}
		instances := decodeToolResult[listInstancesOut](t, raw)
		for _, inst := range instances.Instances {
			if inst.InstanceID == instanceID {
				return len(inst.Documents)
			}
		}
		t.Fatalf("instance %s disappeared from list_instances", instanceID)
		return -1
	}
	waitForCount := func(want int, what string) {
		deadline := time.Now().Add(20 * time.Second)
		last := -1
		for time.Now().Before(deadline) {
			last = countDocuments()
			if last == want {
				return
			}
			time.Sleep(500 * time.Millisecond)
		}
		t.Fatalf("%s: document count never reached %d (last saw %d) -- either the snapshot push or the cleanup helper regressed", what, want, last)
	}

	baseline := countDocuments()

	created := runScript(t, c, instanceID, documentID, `return Connector.CreateProjectDocument().Title;`)
	if created.Status != "success" {
		t.Fatalf("create failed: status=%q return_value=%s", created.Status, created.ReturnValue)
	}
	title := strings.TrimSpace(created.ReturnValue)
	if title == "" {
		t.Fatalf("created document reported no title")
	}

	// Appearance is the issue #30 half: the pushed register lists the new
	// document without any reconnect.
	waitForCount(baseline+1, "after create")

	// Closing is the helper half -- called directly, not via t.Cleanup, so
	// its effect is assertable inside this test.
	closeDocumentByTitle(t, c, instanceID, documentID, title, "")
	waitForCount(baseline, "after close")
}
