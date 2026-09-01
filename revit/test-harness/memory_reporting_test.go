//go:build harness

package harness_test

import (
	"testing"
	"time"
)

// TestListInstancesReportsProcessMemory is the live regression test for issue
// #31's memory-reporting deliverable: the add-in samples its own Revit process
// memory and rides it on the ~10s heartbeat ping, and the broker surfaces it as
// instance.memory in list_instances. Because the sample rides the heartbeat (not
// register), it appears within one ping interval of connecting rather than
// immediately -- so this polls rather than asserting on a single snapshot.
//
// It targets the heartbeat channel specifically (private_mb > 0 is the proof the
// add-in sampled a real process, not that the field merely deserialized); the
// unit tier (MemorySnapshot/PingMessage, and the Go registry survives-re-register
// test) covers the wiring, and this is the only tier that can prove the sample
// actually flows add-in -> ping -> broker -> list_instances end to end.
func TestListInstancesReportsProcessMemory(t *testing.T) {
	c, instanceID, _ := targetDocument(t)

	type memOut struct {
		Instances []struct {
			InstanceID string `json:"instance_id"`
			Memory     *struct {
				PrivateMB    int64 `json:"private_mb"`
				WorkingSetMB int64 `json:"working_set_mb"`
				ManagedMB    int64 `json:"managed_mb"`
			} `json:"memory"`
		} `json:"instances"`
	}

	// A little over two heartbeat intervals (~10s each) so a just-connected
	// instance that hasn't pinged yet still gets a fair chance.
	deadline := time.Now().Add(25 * time.Second)
	for time.Now().Before(deadline) {
		raw, err := c.CallTool("list_instances", map[string]any{}, 10*time.Second)
		if err != nil {
			t.Fatalf("list_instances: %v", err)
		}
		out := decodeToolResult[memOut](t, raw)
		for _, inst := range out.Instances {
			if inst.InstanceID != instanceID || inst.Memory == nil {
				continue
			}
			m := inst.Memory
			t.Logf("instance %s memory: private_mb=%d working_set_mb=%d managed_mb=%d",
				inst.InstanceID, m.PrivateMB, m.WorkingSetMB, m.ManagedMB)
			// private_mb (committed) is the headline signal and is always positive
			// for a live process; a zero here would mean the field round-tripped but
			// the add-in never actually sampled.
			if m.PrivateMB <= 0 {
				t.Fatalf("memory surfaced but private_mb=%d (want > 0) -- the add-in did not sample a real process", m.PrivateMB)
			}
			if m.ManagedMB <= 0 {
				t.Errorf("managed_mb=%d (want > 0) -- the CLR heap is never empty in a running Revit", m.ManagedMB)
			}
			return
		}
		time.Sleep(2 * time.Second)
	}
	t.Fatalf("no memory field appeared in list_instances for instance %s within 25s -- the heartbeat should carry it every ~10s (add-in BridgeHost + broker ping handler)", instanceID)
}
