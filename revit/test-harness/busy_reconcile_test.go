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

	// The first run's result is still retrievable, exactly as if it had been polled.
	polled := decodeToolResult[executeScriptOut](t, callPollExecution(t, c, first.ExecutionID, 1000))
	if polled.Status != "success" {
		t.Errorf("the reconciled first run should poll as success, got %q", polled.Status)
	}
}
