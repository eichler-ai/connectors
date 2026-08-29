//go:build harness

package harness_test

import (
	"encoding/json"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/revit/test-harness/mcpclient"
)

// callPollExecution is the poll_execution counterpart of callExecuteScript.
// The client deadline stays above the requested timeout_ms for the same
// reason callExecuteScriptWith's does: the server owns the wait, and a
// client that gives up first turns an ordinary still-running answer into a
// spurious failure.
func callPollExecution(t *testing.T, c *mcpclient.Client, executionID string, timeoutMs int) json.RawMessage {
	t.Helper()
	raw, err := c.CallTool("poll_execution", map[string]any{
		"execution_id": executionID,
		"timeout_ms":   timeoutMs,
	}, time.Duration(timeoutMs)*time.Millisecond+15*time.Second)
	if err != nil {
		t.Fatalf("poll_execution: %v", err)
	}
	return raw
}

// callCancelExecution requests cooperative cancellation. Deadline sized
// above the broker's own fixed cancel wait (10s, execution.CancelExecution)
// plus margin.
func callCancelExecution(t *testing.T, c *mcpclient.Client, executionID string) json.RawMessage {
	t.Helper()
	raw, err := c.CallTool("cancel_execution", map[string]any{"execution_id": executionID}, 25*time.Second)
	if err != nil {
		t.Fatalf("cancel_execution: %v", err)
	}
	return raw
}

// isTerminalStatus mirrors the broker's own terminal set (PRD §06).
func isTerminalStatus(status string) bool {
	switch status {
	case "success", "error", "cancelled", "unrecoverable":
		return true
	}
	return false
}

// resolveExecutionBestEffort drives executionID to a terminal state: cancel,
// then poll until terminal or the deadline lapses. Used on this test's own
// failure paths AND against a stale run found already occupying the shared
// instance -- a live-learned broker behavior worth stating: the broker's
// busy latch frees only when the stale execution is POLLED (or cancelled) to
// its terminal state. The add-in finishing on its own is not enough; an
// execute_script that answers {busy, execution_id} is handing the caller the
// id to resolve, and an interrupted suite that never does leaves the
// instance answering busy indefinitely. Best-effort by design: it must never
// mask the failure that got us here.
func resolveExecutionBestEffort(t *testing.T, c *mcpclient.Client, executionID string) {
	t.Helper()
	if executionID == "" {
		return
	}
	_ = mustCallTool(t, c, "cancel_execution", map[string]any{"execution_id": executionID}, 25*time.Second)
	deadline := time.Now().Add(90 * time.Second) // past the 60s failsafe of this test's own long-runner
	for time.Now().Before(deadline) {
		raw := mustCallTool(t, c, "poll_execution", map[string]any{"execution_id": executionID, "timeout_ms": 2000}, 20*time.Second)
		var tr toolResult
		if err := json.Unmarshal(raw, &tr); err != nil {
			t.Logf("resolve %s: undecodable poll answer: %v", executionID, err)
			return
		}
		if tr.IsError {
			return // terminal error-side outcome (error/unrecoverable/unknown id) -- the latch is free either way
		}
		var out executeScriptOut
		if err := json.Unmarshal(tr.StructuredContent, &out); err != nil {
			t.Logf("resolve %s: undecodable structuredContent: %v", executionID, err)
			return
		}
		if isTerminalStatus(out.Status) {
			return
		}
		time.Sleep(500 * time.Millisecond)
	}
	t.Logf("resolve %s: never reached a terminal state before the deadline", executionID)
}

// mustCallTool is CallTool with transport errors logged, not fatal --
// resolveExecutionBestEffort runs on failure/cleanup paths where a second
// failure must not mask the first.
func mustCallTool(t *testing.T, c *mcpclient.Client, tool string, args map[string]any, timeout time.Duration) json.RawMessage {
	t.Helper()
	raw, err := c.CallTool(tool, args, timeout)
	if err != nil {
		t.Logf("%s during resolve: %v", tool, err)
		return json.RawMessage(`{}`)
	}
	return raw
}

// executionIDFromErrorText pulls error.detail.execution_id out of a §01
// error record's JSON text -- the only place a wire-failed execute names the
// id it minted (whose broker record deliberately stays non-terminal).
func executionIDFromErrorText(text string) string {
	var envelope struct {
		Error struct {
			Detail struct {
				ExecutionID string `json:"execution_id"`
			} `json:"detail"`
		} `json:"error"`
	}
	if err := json.Unmarshal([]byte(text), &envelope); err != nil {
		return ""
	}
	return envelope.Error.Detail.ExecutionID
}

// TestExecutionLifecycle is the live pin of PRD §06's whole non-inline
// contract, previously untested at any tier end to end (issue #36): the
// pending/running two-shape return, the busy latch, poll_execution, and
// cooperative cancellation resolving to `cancelled` -- then the instance
// genuinely freed for the next script. One flow, because the states only
// mean anything in sequence.
//
// The long-runner loops on its CancellationToken -- exactly the shape
// skill.md tells agents to write -- with a bounded failsafe so a broken
// cancellation path fails THIS test with a clear message instead of
// occupying the shared instance for max_duration_ms.
func TestExecutionLifecycle(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	const script = `
var sw = System.Diagnostics.Stopwatch.StartNew();
while (!CancellationToken.IsCancellationRequested && sw.Elapsed.TotalSeconds < 60) {
  System.Threading.Thread.Sleep(50);
}
CancellationToken.ThrowIfCancellationRequested();
throw new System.TimeoutException("failsafe: cancellation never arrived within 60s");
`

	// EVERY execution id this test ever learns gets resolved on the way out,
	// registered up front, appended as ids appear -- a failure at any point
	// must not strand a runner non-terminal and the instance busy for whoever
	// comes next (the exact cascade this test's first live run produced, and
	// again via its own retry path's answer once). Cheap no-ops for runs that
	// finished properly.
	var idsToResolve []string
	t.Cleanup(func() {
		for _, id := range idsToResolve {
			resolveExecutionBestEffort(t, c, id)
		}
	})

	// startRun starts the long-runner and returns the decoded answer WITHOUT
	// fataling on an error-shaped one: an error answer (e.g. wire_call_failed,
	// whose broker record stays non-terminal by design) names the minted-and-
	// possibly-stranded id only in error.detail.execution_id, and that id must
	// be captured for resolution before anything gets to fail the test.
	startRun := func() (executeScriptOut, string) {
		raw := callExecuteScriptWith(t, c, instanceID, documentID, script, map[string]any{"timeout_ms": 2000})
		var tr toolResult
		if err := json.Unmarshal(raw, &tr); err != nil {
			t.Fatalf("decode tool envelope: %v\nraw: %s", err, raw)
		}
		if tr.IsError {
			text := "(no content)"
			if len(tr.Content) > 0 {
				text = tr.Content[0].Text
			}
			if id := executionIDFromErrorText(text); id != "" {
				idsToResolve = append(idsToResolve, id)
			}
			return executeScriptOut{}, text
		}
		var out executeScriptOut
		if err := json.Unmarshal(tr.StructuredContent, &out); err != nil {
			t.Fatalf("decode structuredContent: %v\nraw: %s", err, tr.StructuredContent)
		}
		if out.ExecutionID != "" {
			idsToResolve = append(idsToResolve, out.ExecutionID)
		}
		return out, ""
	}

	// timeout_ms deliberately far below the script's own runtime: the call
	// must come back with the non-terminal shape, not a completed result.
	started, errText := startRun()
	if errText != "" {
		t.Fatalf("starting the long-runner failed outright: %s", errText)
	}
	if started.Status == "busy" {
		// A stale non-terminal run already occupies the shared instance (an
		// interrupted earlier suite that never polled its runner to terminal
		// -- see resolveExecutionBestEffort's note). Resolve it and retry
		// once: self-healing here keeps one aborted run from failing every
		// later suite against the same live session.
		t.Logf("instance busy with stale execution %s; resolving and retrying once", started.ExecutionID)
		resolveExecutionBestEffort(t, c, started.ExecutionID)
		var retryErr string
		started, retryErr = startRun()
		if retryErr != "" {
			t.Fatalf("retry after resolving the stale execution failed: %s", retryErr)
		}
	}
	if started.Status != "pending" && started.Status != "running" {
		t.Fatalf("a script outliving timeout_ms must return pending/running, got %q (execution_id: %s, output: %s)", started.Status, started.ExecutionID, started.Output)
	}
	if started.ExecutionID == "" {
		t.Fatalf("non-terminal status carried no execution_id, so nothing can ever poll it: %+v", started)
	}

	// Instance busy state (PRD §06): a second execute_script against the same
	// instance names the run already in flight rather than queuing silently.
	second := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID, `return 1;`,
		map[string]any{"timeout_ms": 2000}))
	if second.Status != "busy" {
		t.Fatalf("a second execute_script during a live run must report busy, got %q", second.Status)
	}
	if second.ExecutionID != started.ExecutionID {
		t.Fatalf("busy must point at the in-flight execution (%s), got %s", started.ExecutionID, second.ExecutionID)
	}

	// poll_execution: still the non-terminal shape while the script loops.
	polled := decodeToolResult[executeScriptOut](t, callPollExecution(t, c, started.ExecutionID, 1000))
	if polled.Status != "pending" && polled.Status != "running" {
		t.Fatalf("polling a live run must report pending/running, got %q (output: %s)", polled.Status, polled.Output)
	}

	// Cooperative cancellation: the script observes its token within ~50ms,
	// so the cancel call itself normally comes back already terminal; a
	// non-terminal answer is legal (the broker reports whatever the add-in
	// said) and resolved by polling to the terminal state.
	cancelled := decodeToolResult[executeScriptOut](t, callCancelExecution(t, c, started.ExecutionID))
	status := cancelled.Status
	deadline := time.Now().Add(20 * time.Second)
	for status != "cancelled" && time.Now().Before(deadline) {
		switch status {
		case "pending", "running", "busy":
			status = decodeToolResult[executeScriptOut](t, callPollExecution(t, c, started.ExecutionID, 1000)).Status
		default:
			t.Fatalf("cancellation of a token-observing script resolved to %q, want cancelled -- PRD §06's cooperative path is broken (unrecoverable here would falsely brick the shared instance)", status)
		}
	}
	if status != "cancelled" {
		t.Fatalf("execution never reached cancelled within the deadline; last status %q", status)
	}

	// And the instance is genuinely freed -- the busy latch cleared with the
	// terminal state, so ordinary work proceeds.
	after := runScript(t, c, instanceID, documentID, `return "freed";`)
	if after.Status != "success" {
		t.Fatalf("instance still not usable after a cancelled run: status=%q output=%s", after.Status, after.Output)
	}
}
