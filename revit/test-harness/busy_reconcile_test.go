//go:build harness

package harness_test

import (
	"testing"
	"time"
)

// TestAnUnpolledCompletionDoesNotAnswerBusy pins issue #54 live: a script
// that outlives its timeout_ms and then finishes with NOBODY polling used to
// leave the broker's busy latch set, so the next execute_script answered
// `busy` about a run that was already over. The broker now reconciles with a
// zero-wait poll before answering, so the second script simply runs.
func TestAnUnpolledCompletionDoesNotAnswerBusy(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	first := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID, `
System.Threading.Thread.Sleep(4000);
return "slow-done";
`, map[string]any{"timeout_ms": 1000}))
	if first.Status != "pending" && first.Status != "running" {
		t.Skipf("the slow script finished inside timeout_ms (status %q); the unpolled-completion window never opened", first.Status)
	}

	// Let it finish add-in-side. Deliberately no poll_execution here.
	time.Sleep(6 * time.Second)

	second := runScript(t, c, instanceID, documentID, `return "second-ran";`)
	if second.Status != "success" {
		t.Fatalf("the second script after an unpolled completion was expected to run, got status %q (%s) -- the busy-latch reconciliation (#54) is not working", second.Status, second.diag())
	}
	if second.ExecutionID == first.ExecutionID {
		t.Fatalf("the second call returned the FIRST execution's id %q: that is the pre-#54 busy answer", second.ExecutionID)
	}

	// A busy answer against a script that IS still running must be prompt: the reconciliation poll is
	// answered from the add-in's record with no window inventory, so it costs one round trip, not the
	// ~1.5s inventory pass the independent review of #164 found the first version paying.
	running := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID, `
System.Threading.Thread.Sleep(4000);
return "slow-again";
`, map[string]any{"timeout_ms": 1000}))
	if running.Status == "pending" || running.Status == "running" {
		started := time.Now()
		busy := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID, `return "should-be-busy";`, map[string]any{"timeout_ms": 1000}))
		elapsed := time.Since(started)
		if busy.Status != "busy" || busy.ExecutionID != running.ExecutionID {
			t.Fatalf("expected busy pointing at %q, got %+v", running.ExecutionID, busy)
		}
		if elapsed > 900*time.Millisecond {
			t.Errorf("busy answer took %s; the reconciliation poll should be one round trip, not an inventory pass", elapsed)
		}
		if p := decodeToolResult[executeScriptOut](t, callPollExecution(t, c, running.ExecutionID, 8000)); p.Status != "success" {
			t.Fatalf("draining the slow run: %+v", p)
		}
	}

	// The first run's result is still retrievable, exactly as if it had been polled.
	polled := decodeToolResult[executeScriptOut](t, callPollExecution(t, c, first.ExecutionID, 1000))
	if polled.Status != "success" {
		t.Errorf("the reconciled first run should poll as success, got %q", polled.Status)
	}
}
