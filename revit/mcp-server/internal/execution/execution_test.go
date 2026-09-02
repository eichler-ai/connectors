package execution

import (
	"context"
	"encoding/json"
	"fmt"
	"net"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/transport"
)

// fakeInstance simulates the add-in side of one instance's wire connection:
// a transport.Conn whose request handler is scriptable per test.
type fakeInstance struct {
	conn     *transport.Conn
	requests int32
}

func newFakeInstance(t *testing.T, handler transport.RequestHandler) (*fakeInstance, *transport.Conn) {
	t.Helper()
	brokerSide, addinSide := net.Pipe()
	brokerConn := transport.NewConn(brokerSide)
	addinConn := transport.NewConn(addinSide)

	fi := &fakeInstance{conn: addinConn}
	addinConn.SetRequestHandler(func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		atomic.AddInt32(&fi.requests, 1)
		return handler(ctx, method, params)
	})

	go brokerConn.Serve()
	go addinConn.Serve()
	t.Cleanup(func() {
		brokerConn.Close()
		addinConn.Close()
	})
	return fi, brokerConn
}

func TestExecuteScriptCompletesInline(t *testing.T) {
	fi, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusSuccess, ExecutionID: p["execution_id"].(string), Output: "42"}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	res, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "return 42;", 5000, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("unexpected diag error: %+v", drec)
	}
	if res.Status != StatusSuccess {
		t.Errorf("Status = %q, want success", res.Status)
	}
	if res.Output != "42" {
		t.Errorf("Output = %q, want 42", res.Output)
	}
	if atomic.LoadInt32(&fi.requests) != 1 {
		t.Errorf("expected exactly 1 wire request, got %d", fi.requests)
	}
}

func TestExecuteScriptReturnsPendingOnSlowScript(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusPending, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	res, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "sleep forever", 100, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("unexpected diag error: %+v", drec)
	}
	if res.Status != StatusPending {
		t.Errorf("Status = %q, want pending", res.Status)
	}
	if res.ExecutionID == "" {
		t.Errorf("ExecutionID should be set on a pending result")
	}
}

// TestExecuteScriptForwardsOverwriteOutputFilesAndFiles is a regression test
// for PRD §09's file-exchange wire fields: overwriteOutputFiles must reach
// the add-in as "overwrite_output_files" in the execute_script params (the
// same mechanism document_id already uses to reach the wire), and a files[]
// array in the add-in's reply must round-trip into Result.Files.
func TestExecuteScriptForwardsOverwriteOutputFilesAndFiles(t *testing.T) {
	var gotParams map[string]any
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		json.Unmarshal(params, &gotParams)
		return map[string]any{
			"status":       "success",
			"execution_id": gotParams["execution_id"],
			"files": []map[string]any{
				{"name": "view.png", "path": "exports/view.png", "status": "published"},
				{"name": "clash.png", "path": "exports/clash.png", "status": "failed", "message": "already exists; overwrite_output_files is false"},
			},
		}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	res, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "Publish(\"a.png\");", 5000, 60000, ScriptOptions{OverwriteOutputFiles: true})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	if v, ok := gotParams["overwrite_output_files"].(bool); !ok || !v {
		t.Errorf("params[\"overwrite_output_files\"] = %+v, want true", gotParams["overwrite_output_files"])
	}

	if len(res.Files) != 2 {
		t.Fatalf("Files = %+v, want 2 entries", res.Files)
	}
	if res.Files[0] != (FileRecord{Name: "view.png", Path: "exports/view.png", Status: "published"}) {
		t.Errorf("Files[0] = %+v", res.Files[0])
	}
	if res.Files[1] != (FileRecord{Name: "clash.png", Path: "exports/clash.png", Status: "failed", Message: "already exists; overwrite_output_files is false"}) {
		t.Errorf("Files[1] = %+v", res.Files[1])
	}
}

func TestExecuteScriptSecondCallReturnsBusy(t *testing.T) {
	block := make(chan struct{})
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		<-block
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	type outcome struct {
		res  *Result
		drec interface{}
	}
	done := make(chan outcome, 1)
	go func() {
		res, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "long script", 60000, 600000, ScriptOptions{})
		done <- outcome{res, drec}
	}()

	// Give the first call time to register itself as the active execution
	// for inst-1 before we issue the second one.
	time.Sleep(100 * time.Millisecond)

	res2, drec2 := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "second script", 5000, 60000, ScriptOptions{})
	close(block)

	if drec2 != nil {
		t.Fatalf("unexpected diag error: %+v", drec2)
	}
	if res2.Status != StatusBusy {
		t.Fatalf("Status = %q, want busy", res2.Status)
	}

	first := <-done
	if res2.ExecutionID != first.res.ExecutionID {
		t.Errorf("busy execution_id = %q, want it to match the in-flight one %q", res2.ExecutionID, first.res.ExecutionID)
	}
}

func TestExecuteScriptUnknownInstance(t *testing.T) {
	m := NewManager()
	_, drec := m.ExecuteScript(context.Background(), "ghost", "doc-1", "1+1", 1000, 60000, ScriptOptions{})
	if drec == nil {
		t.Fatal("expected diag error for unknown instance")
	}
	if drec.Code != "instance-not-found" {
		t.Errorf("Code = %q, want instance-not-found", drec.Code)
	}
	if drec.Detail["instance_id"] != "ghost" {
		t.Errorf("Detail should name the instance_id, got %+v", drec.Detail)
	}
}

func TestPollExecutionUnknownID(t *testing.T) {
	m := NewManager()
	_, drec := m.PollExecution(context.Background(), "exec-does-not-exist", 1000)
	if drec == nil {
		t.Fatal("expected diag error for unknown execution_id")
	}
	if drec.Code != "unknown-execution-id" {
		t.Errorf("Code = %q, want unknown-execution-id", drec.Code)
	}
}

func TestPollExecutionResolvesToTerminal(t *testing.T) {
	var callCount int32
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		id := p["execution_id"].(string)
		n := atomic.AddInt32(&callCount, 1)
		if method == "execute_script" {
			return Result{Status: StatusRunning, ExecutionID: id}, nil
		}
		if n < 3 {
			return Result{Status: StatusRunning, ExecutionID: id}, nil
		}
		return Result{Status: StatusSuccess, ExecutionID: id, Output: "done"}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}
	if start.Status != StatusRunning {
		t.Fatalf("Status = %q, want running", start.Status)
	}

	var final *Result
	for i := 0; i < 5; i++ {
		res, drec := m.PollExecution(context.Background(), start.ExecutionID, 50)
		if drec != nil {
			t.Fatalf("PollExecution: %+v", drec)
		}
		if IsTerminal(res.Status) {
			final = res
			break
		}
	}
	if final == nil {
		t.Fatal("execution never reached a terminal state")
	}
	if final.Status != StatusSuccess || final.Output != "done" {
		t.Errorf("final = %+v", final)
	}

	// A follow-up execute_script on the same instance must succeed now that
	// the prior execution is terminal (busy state cleared).
	res, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "next", 50, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript after terminal poll: %+v", drec)
	}
	if res.Status == StatusBusy {
		t.Errorf("instance should not still be busy after prior execution went terminal")
	}
}

func TestPollExecutionAfterTerminalReturnsCachedResultWithoutWireCall(t *testing.T) {
	fi, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusSuccess, ExecutionID: p["execution_id"].(string), Output: "ok"}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	res, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "fast", 5000, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}
	before := atomic.LoadInt32(&fi.requests)

	polled, drec := m.PollExecution(context.Background(), res.ExecutionID, 1000)
	if drec != nil {
		t.Fatalf("PollExecution: %+v", drec)
	}
	if polled.Status != StatusSuccess || polled.Output != "ok" {
		t.Errorf("polled = %+v", polled)
	}
	if atomic.LoadInt32(&fi.requests) != before {
		t.Errorf("polling an already-terminal execution should not hit the wire again")
	}
}

func TestCancelExecutionForwardsAndSettles(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		id := p["execution_id"].(string)
		if method == "execute_script" {
			return Result{Status: StatusRunning, ExecutionID: id}, nil
		}
		if method == "cancel_execution" {
			return Result{Status: StatusCancelled, ExecutionID: id}, nil
		}
		t.Fatalf("unexpected method %q", method)
		return nil, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "loop forever", 50, 600000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	res, drec := m.CancelExecution(context.Background(), start.ExecutionID)
	if drec != nil {
		t.Fatalf("CancelExecution: %+v", drec)
	}
	if res.Status != StatusCancelled {
		t.Errorf("Status = %q, want cancelled", res.Status)
	}

	// Instance should no longer be busy.
	res2, drec2 := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "fresh", 50, 60000, ScriptOptions{})
	if drec2 != nil {
		t.Fatalf("ExecuteScript after cancel: %+v", drec2)
	}
	if res2.Status == StatusBusy {
		t.Error("instance should not be busy after cancellation resolved to a terminal state")
	}
}

func TestCancelExecutionUnknownID(t *testing.T) {
	m := NewManager()
	_, drec := m.CancelExecution(context.Background(), "exec-nope")
	if drec == nil || drec.Code != "unknown-execution-id" {
		t.Fatalf("got %+v, want unknown-execution-id", drec)
	}
}

// The OTHER branch of fromRPCError, which had no coverage at all until issue #69
// went looking for it. When the add-in sends an error with no `data` record, the
// broker synthesises a generic `add-in-error` -- so a record-less add-in error does
// not merely arrive without a code, it arrives stamped with one that says only
// "something went wrong over there". That is what every C#-side param error looked
// like before #69, and pinning it here is what makes the passthrough test above
// mean something: without this, a change that dropped `data` on the floor entirely
// would still leave that test green.
func TestWireErrorWithoutDataSynthesisesAGenericRecord(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		return nil, &transport.RPCError{
			Code:    transport.ErrCodeInvalidParams,
			Message: "params.script must be a non-empty string.",
			Data:    nil,
		}
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	_, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "boom", 1000, 60000, ScriptOptions{})
	if drec == nil {
		t.Fatal("expected diag error")
	}
	if drec.Code != "add-in-error" {
		t.Errorf("Code = %q, want the synthesised add-in-error fallback", drec.Code)
	}
	if len(drec.Remedy) != 0 {
		t.Errorf("Remedy = %v, want empty: the synthesised record cannot know a next step, which is the whole cost of the add-in not sending its own", drec.Remedy)
	}
}

func TestWireErrorPropagatesDiagnosticData(t *testing.T) {
	addinRecord := diag.New(diag.SeverityError, "revit-api-exception", "mcp-bridge.core.execution", "System.InvalidOperationException: document is not active")
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		return nil, &transport.RPCError{
			Code:    transport.ErrCodeInternalError,
			Message: "script threw",
			Data:    addinRecord,
		}
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	_, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "boom", 1000, 60000, ScriptOptions{})
	if drec == nil {
		t.Fatal("expected diag error")
	}
	if drec.Code != "revit-api-exception" {
		t.Errorf("Code = %q, want the add-in's own code to pass through unwrapped", drec.Code)
	}
}

func TestInstanceDisconnectedDuringPoll(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	m.DetachInstance("inst-1", conn)

	_, drec2 := m.PollExecution(context.Background(), start.ExecutionID, 1000)
	if drec2 == nil || drec2.Code != "instance-disconnected" {
		t.Fatalf("got %+v, want instance-disconnected", drec2)
	}
}

// TestReconnectClearsStaleBusyState is a regression test: DetachInstance
// must clear activeByInstance, not just conns, or a reconnect leaves the
// instance permanently "busy" pointing at an execution that will never
// complete (its owning connection is gone) — every subsequent
// execute_script against that instance_id would return {status:"busy"}
// forever, with no recovery short of restarting the broker.
func TestReconnectClearsStaleBusyState(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	_, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	// The add-in reconnects: its old connection is detached...
	m.DetachInstance("inst-1", conn)

	// ...and a fresh execute_script against the same instance_id must
	// succeed, not report busy against the now-orphaned execution.
	_, conn2 := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusSuccess, ExecutionID: p["execution_id"].(string)}, nil
	})
	m.AttachInstance("inst-1", conn2)

	res, drec2 := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "retry", 1000, 60000, ScriptOptions{})
	if drec2 != nil {
		t.Fatalf("ExecuteScript after reconnect: %+v, want success (not wedged busy)", drec2)
	}
	if res.Status != StatusSuccess {
		t.Errorf("Status = %q, want success — reconnect must free the instance", res.Status)
	}
}

// TestForwardExistingWireErrorDoesNotSettleTerminal is a regression test
// for a bug introduced by an earlier fix attempt: settling an execution to
// a terminal error on a bare wire-level failure (network hiccup, context
// timeout) asserts an outcome the broker doesn't actually know — the add-in
// may genuinely still be running the script. Falsely settling it would
// permanently block the add-in's own ring-buffer replay (PRD §05) from
// ever correcting it after a reconnect. The execution must stay
// non-terminal on a wire failure; recovery from a permanently-busy instance
// comes from DetachInstance (see TestReconnectClearsStaleBusyState), not
// from guessing a terminal status here.
func TestForwardExistingWireErrorDoesNotSettleTerminal(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	// Force the next wire call to fail without telling the Manager to
	// DetachInstance — simulating a genuine wire failure, not a clean
	// disconnect.
	conn.Close()

	_, drec2 := m.PollExecution(context.Background(), start.ExecutionID, 100)
	if drec2 == nil {
		t.Fatal("expected a diag error from the failed wire call")
	}

	m.mu.Lock()
	rec := m.executions[start.ExecutionID]
	m.mu.Unlock()
	if rec == nil {
		t.Fatal("execution record disappeared after a wire failure — it should be left as-is, not deleted or settled")
	}
	if IsTerminal(rec.status) {
		t.Errorf("status = %q, want it to stay non-terminal — a wire failure doesn't mean the add-in didn't actually run the script", rec.status)
	}
}

// TestUnrecoverableLatchesInstance is a regression test: per PRD §06, once
// an execution settles to "unrecoverable" the whole instance must stay
// rejected for execute_script until a Revit restart — not just free up
// activeByInstance like any other terminal status, which would forward a
// new script to a Revit instance already known to be wedged. Critically,
// the latch must survive a same-instance_id reconnect: per PRD §05,
// instance_id is stable for the life of the Revit *process*, independent of
// any one connection, so a reconnect under the same id is the same wedged
// process reappearing after a network blip — not a recovery. An earlier fix
// attempt cleared the latch on every DetachInstance, which silently
// un-latched a still-wedged instance the moment its connection blipped;
// that was wrong and is asserted against explicitly here.
func TestUnrecoverableLatchesInstance(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	// Simulate the add-in's cancellation grace period lapsing.
	m.settle("inst-1", start.ExecutionID, &Result{Status: StatusUnrecoverable, ExecutionID: start.ExecutionID})

	_, drec2 := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "another", 1000, 60000, ScriptOptions{})
	if drec2 == nil || drec2.Code != "instance-unrecoverable" {
		t.Fatalf("got %+v, want instance-unrecoverable", drec2)
	}

	// A network blip and reconnect under the SAME instance_id — the latch
	// must survive this, since it's still the same wedged Revit process.
	m.DetachInstance("inst-1", conn)
	m.AttachInstance("inst-1", conn)
	_, drec3 := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "after reconnect", 1000, 60000, ScriptOptions{})
	if drec3 == nil || drec3.Code != "instance-unrecoverable" {
		t.Fatalf("got %+v, want instance-unrecoverable to survive a same-id reconnect", drec3)
	}
}

// TestUnrecoverableDoesNotAffectADifferentInstanceID confirms the latch is
// genuinely scoped per instance_id: a real Revit restart mints a brand-new
// instance_id (PRD §05), a different map key the old latch never touches,
// so the new "instance" (in practice, the same Revit process post-restart)
// must not be rejected.
func TestUnrecoverableDoesNotAffectADifferentInstanceID(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}
	m.settle("inst-1", start.ExecutionID, &Result{Status: StatusUnrecoverable, ExecutionID: start.ExecutionID})
	m.DetachInstance("inst-1", conn)

	_, conn2 := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusSuccess, ExecutionID: p["execution_id"].(string)}, nil
	})
	m.AttachInstance("inst-1-restarted", conn2)

	res, drec2 := m.ExecuteScript(context.Background(), "inst-1-restarted", "doc-1", "post-restart", 1000, 60000, ScriptOptions{})
	if drec2 != nil {
		t.Fatalf("ExecuteScript against a fresh instance_id: %+v, want it to succeed", drec2)
	}
	if res.Status != StatusSuccess {
		t.Errorf("Status = %q, want success", res.Status)
	}
}

// TestSettleDoesNotRegressAlreadyTerminalStatus is a regression test:
// concurrent poll_execution/cancel_execution calls can both be in flight
// against the same execution; a later (possibly stale) response must never
// overwrite an already-settled terminal result.
func TestSettleDoesNotRegressAlreadyTerminalStatus(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	m.settle("inst-1", start.ExecutionID, &Result{Status: StatusSuccess, ExecutionID: start.ExecutionID, Output: "first"})
	m.settle("inst-1", start.ExecutionID, &Result{Status: StatusCancelled, ExecutionID: start.ExecutionID})

	m.mu.Lock()
	rec := m.executions[start.ExecutionID]
	m.mu.Unlock()
	if rec.status != StatusSuccess {
		t.Errorf("status = %q, want success — the first settle should win, not regress to cancelled", rec.status)
	}
}

// TestMaxDurationAutoCancelsUnpolledExecution is a regression test for PRD
// §06's max_duration_ms: "the broker auto-issues the same cancellation
// signal on the agent's behalf, so a script nobody's actively polling
// doesn't sit forever silently occupying the instance."
func TestMaxDurationAutoCancelsUnpolledExecution(t *testing.T) {
	var cancelCalled int32
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		if method == "cancel_execution" {
			atomic.AddInt32(&cancelCalled, 1)
			return Result{Status: StatusCancelled, ExecutionID: p["execution_id"].(string)}, nil
		}
		return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	_, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 100 /* maxDurationMs */, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if atomic.LoadInt32(&cancelCalled) > 0 {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("expected max_duration_ms to trigger an automatic cancel_execution wire call")
}

// fakeTimer stands in for *time.Timer in tests below: newManagerWithFakeClock
// replaces Manager.afterFunc with one that records the callback instead of
// actually scheduling it against the wall clock, and a test fires callbacks
// itself via fire() — deterministic, no real multi-second sleeps.
type fakeAfterFunc struct {
	mu    sync.Mutex
	calls []func()
}

func (f *fakeAfterFunc) schedule(_ time.Duration, cb func()) *time.Timer {
	f.mu.Lock()
	f.calls = append(f.calls, cb)
	f.mu.Unlock()
	return nil
}

// fireAll synchronously runs every callback scheduled so far, in the order
// scheduled (simulating every outstanding timer having elapsed). Additional
// callbacks scheduled *during* a fired callback (e.g. CancelExecution's own
// grace-escalation timer, scheduled from inside scheduleAutoCancel's fired
// callback) are also fired, so a single fireAll drains a whole chain.
func (f *fakeAfterFunc) fireAll() {
	for {
		f.mu.Lock()
		if len(f.calls) == 0 {
			f.mu.Unlock()
			return
		}
		cb := f.calls[0]
		f.calls = f.calls[1:]
		f.mu.Unlock()
		cb()
	}
}

func newManagerWithFakeClock() (*Manager, *fakeAfterFunc) {
	m := NewManager()
	fa := &fakeAfterFunc{}
	m.afterFunc = fa.schedule
	return m, fa
}

// TestCancelExecutionEscalatesToUnrecoverableAfterGracePeriod is the
// regression test for bug 2: a connection that stays open but never
// responds to a cancel_execution wire call (the add-in is wedged, per PRD
// §06's "fallback, for scripts that don't cooperate") must not leave the
// instance permanently reporting busy. Once the cancellation grace period
// lapses without the execution reaching a terminal state, the broker itself
// must flip the instance to unrecoverable, via the same settle/latch
// mechanism used for an add-in-reported StatusUnrecoverable.
func TestCancelExecutionEscalatesToUnrecoverableAfterGracePeriod(t *testing.T) {
	// block simulates a live-but-unresponsive add-in: the handler for
	// cancel_execution never returns on its own. RequestHandler is always
	// invoked with context.Background() (see transport/conn.go), so it
	// can't observe the caller's ctx being cancelled — the test itself
	// unblocks this at cleanup so the handler goroutine doesn't leak past
	// the end of the test.
	block := make(chan struct{})
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		id := p["execution_id"].(string)
		if method == "execute_script" {
			return Result{Status: StatusRunning, ExecutionID: id}, nil
		}
		<-block
		return Result{Status: StatusCancelled, ExecutionID: id}, nil
	})
	t.Cleanup(func() { close(block) })
	m, fa := newManagerWithFakeClock()
	m.AttachInstance("inst-1", conn)

	// maxDurationMs is 0 (disabled) so ExecuteScript doesn't ALSO register
	// its own scheduleAutoCancel timer on the fake clock — this test wants
	// exactly one scheduled callback (CancelExecution's own grace
	// escalation) so fireAll below doesn't end up synchronously running an
	// unrelated auto-cancel that blocks for its own full wire budget.
	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "wedge forever", 50, 0, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	cancelCtx, cancelCause := context.WithCancel(context.Background())
	cancelDone := make(chan struct{})
	go func() {
		// This call will itself hang until cancelCtx is done (the fake
		// add-in never answers); that's fine, it's meant to model a wire
		// call that never completes — the escalation must not depend on
		// this call ever returning.
		_, _ = m.CancelExecution(cancelCtx, start.ExecutionID)
		close(cancelDone)
	}()

	// Give CancelExecution a moment to register its grace-escalation timer
	// before firing it.
	deadline := time.Now().Add(2 * time.Second)
	for {
		fa.mu.Lock()
		n := len(fa.calls)
		fa.mu.Unlock()
		if n > 0 {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("CancelExecution never scheduled a grace-escalation timer")
		}
		time.Sleep(time.Millisecond)
	}

	// Simulate the grace period lapsing: fire the scheduled callback
	// directly instead of waiting on a real timer.
	fa.fireAll()
	cancelCause()
	<-cancelDone

	// The instance must now be unrecoverable, not stuck busy forever.
	_, drec2 := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "another", 1000, 60000, ScriptOptions{})
	if drec2 == nil || drec2.Code != "instance-unrecoverable" {
		t.Fatalf("ExecuteScript after grace-period escalation: %+v, want instance-unrecoverable", drec2)
	}

	m.mu.Lock()
	rec := m.executions[start.ExecutionID]
	m.mu.Unlock()
	if rec == nil || rec.status != StatusUnrecoverable {
		t.Fatalf("execution status = %+v, want it settled to unrecoverable by the grace-period escalation", rec)
	}
}

// TestCancelExecutionGraceEscalationIsNoOpIfAlreadyTerminal confirms the
// grace-period escalation added for bug 2 doesn't regress a normal,
// cooperative cancellation: if the add-in answers cancel_execution with a
// terminal status well before the grace period elapses, firing the
// (already-obsolete) grace timer afterward must not clobber that result.
func TestCancelExecutionGraceEscalationIsNoOpIfAlreadyTerminal(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		id := p["execution_id"].(string)
		if method == "execute_script" {
			return Result{Status: StatusRunning, ExecutionID: id}, nil
		}
		return Result{Status: StatusCancelled, ExecutionID: id}, nil
	})
	m, fa := newManagerWithFakeClock()
	m.AttachInstance("inst-1", conn)

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "cooperative", 50, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}
	res, drec2 := m.CancelExecution(context.Background(), start.ExecutionID)
	if drec2 != nil {
		t.Fatalf("CancelExecution: %+v", drec2)
	}
	if res.Status != StatusCancelled {
		t.Fatalf("Status = %q, want cancelled", res.Status)
	}

	// Fire the grace-escalation timer scheduled by CancelExecution; the
	// execution already settled to cancelled, so this must be a no-op.
	fa.fireAll()

	m.mu.Lock()
	rec := m.executions[start.ExecutionID]
	m.mu.Unlock()
	if rec.status != StatusCancelled {
		t.Errorf("status = %q after firing an obsolete grace timer, want it to stay cancelled", rec.status)
	}
	if m.unrecoverable["inst-1"] {
		t.Error("instance must not be latched unrecoverable when cancellation already resolved cooperatively")
	}
}

func TestStatusForInstance_IdleWhenNoActiveExecution(t *testing.T) {
	m := NewManager()
	if got := m.StatusForInstance("inst-1"); got != StatusIdle {
		t.Errorf("StatusForInstance = %q, want idle for an instance with no active execution", got)
	}
}

func TestStatusForInstance_PendingWhileQueued(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusPending, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	_, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "sleep forever", 100, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	if got := m.StatusForInstance("inst-1"); got != StatusPending {
		t.Errorf("StatusForInstance = %q, want pending", got)
	}
}

func TestStatusForInstance_BusyWhileRunning(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	_, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 100, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	if got := m.StatusForInstance("inst-1"); got != StatusBusy {
		t.Errorf("StatusForInstance = %q, want busy for an execution the add-in reported as running", got)
	}
}

func TestStatusForInstance_UnrecoverableBeatsEverythingElse(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}
	// Simulate the add-in's cancellation grace period lapsing -- the
	// instance is still "active" (activeByInstance still points at this
	// execution) but must report unrecoverable, not pending/busy.
	m.settle("inst-1", start.ExecutionID, &Result{Status: StatusUnrecoverable, ExecutionID: start.ExecutionID})

	if got := m.StatusForInstance("inst-1"); got != StatusUnrecoverable {
		t.Errorf("StatusForInstance = %q, want unrecoverable", got)
	}
}

// TestExecuteScriptForwardsConfirmLifecycleActions pins PRD §14's confirmation
// flag onto the wire. The add-in is where the decision is actually made (it is
// the only side that can see what the script's compiled form touches), so the
// broker's whole job here is to transmit the request's answer faithfully —
// which makes "did it reach the params, with the right name and the right
// value" the entire contract. Both values are asserted, not just true: a flag
// that silently defaults to true when the caller said false would be the worst
// possible failure of a confirmation gate, and `omitempty` on the tool input
// makes false the value most likely to get lost.
func TestExecuteScriptForwardsConfirmLifecycleActions(t *testing.T) {
	for _, confirm := range []bool{false, true} {
		var gotParams map[string]any
		_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
			json.Unmarshal(params, &gotParams)
			return map[string]any{"status": "success", "execution_id": gotParams["execution_id"]}, nil
		})
		m := NewManager()
		m.AttachInstance("inst-1", conn)

		_, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "Document.Save();", 5000, 60000,
			ScriptOptions{ConfirmLifecycleActions: confirm})
		if drec != nil {
			t.Fatalf("ExecuteScript(confirm=%v): %+v", confirm, drec)
		}

		got, ok := gotParams["confirm_lifecycle_actions"].(bool)
		if !ok {
			t.Fatalf("params has no bool \"confirm_lifecycle_actions\"; got %+v", gotParams)
		}
		if got != confirm {
			t.Errorf("params[\"confirm_lifecycle_actions\"] = %v, want %v", got, confirm)
		}
	}
}

// TestDetachIgnoresStaleConnection is the regression test for the
// reconnect-overlap race (v1 integrated review): a half-open connection's
// late teardown, running after the add-in already re-registered the same
// instance_id on a new connection, must not tear down the live replacement.
func TestDetachIgnoresStaleConnection(t *testing.T) {
	okHandler := func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusSuccess, ExecutionID: p["execution_id"].(string), Output: "from-B"}, nil
	}
	_, connA := newFakeInstance(t, okHandler)
	_, connB := newFakeInstance(t, okHandler)
	m := NewManager()

	if displaced := m.AttachInstance("inst-1", connA); displaced != nil {
		t.Fatalf("first attach displaced %v, want nil", displaced)
	}
	// Re-attaching the SAME connection must not report it as displaced —
	// the caller would close it, killing the live connection.
	if displaced := m.AttachInstance("inst-1", connA); displaced != nil {
		t.Fatalf("same-conn re-attach displaced %v, want nil", displaced)
	}
	// The redial: connB replaces connA, and connA is handed back for the
	// caller to close.
	if displaced := m.AttachInstance("inst-1", connB); displaced != connA {
		t.Fatalf("attach of the new connection displaced %v, want the old connection", displaced)
	}

	// The stale connection's late teardown must be a no-op...
	if m.DetachInstance("inst-1", connA) {
		t.Fatal("detach keyed by a stale connection must report false and change nothing")
	}
	// ...leaving the instance routable through the new connection.
	res, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "1+1", 1000, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript after stale detach: %+v", drec)
	}
	if res.Status != StatusSuccess || res.Output != "from-B" {
		t.Errorf("res = %+v, want success routed to the new connection", res)
	}

	// The current connection's own teardown still detaches normally.
	if !m.DetachInstance("inst-1", connB) {
		t.Fatal("detach keyed by the current connection must apply")
	}
	if _, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "1+1", 1000, 60000, ScriptOptions{}); drec == nil || drec.Code != "instance-not-found" {
		t.Fatalf("got %+v, want instance-not-found after the current connection detached", drec)
	}
}

// TestCloseInstanceConnClosesTheAttachedConnection covers the prune sweep's
// half of the split-brain fix: closing a pruned instance's socket is what
// forces its add-in back through the reconnect/re-register path instead of
// leaving it executable-but-invisible.
func TestCloseInstanceConnClosesTheAttachedConnection(t *testing.T) {
	m := NewManager()
	m.CloseInstanceConn("never-registered") // must be a safe no-op

	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		return Result{Status: StatusSuccess}, nil
	})
	m.AttachInstance("inst-1", conn)
	m.CloseInstanceConn("inst-1")

	// The connection is closed, so a wire call through it must fail rather
	// than hang or succeed.
	_, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "1+1", 500, 60000, ScriptOptions{})
	if drec == nil {
		t.Fatal("expected a wire failure through a closed connection")
	}
	if drec.Code != "wire-call-failed" {
		t.Errorf("Code = %q, want wire-call-failed", drec.Code)
	}
}

// TestSettledExecutionRecordsAreBounded pins the terminal-record cache
// bound (v1 integrated review: the primary's executions map previously grew
// by one record per execute_script forever).
func TestSettledExecutionRecordsAreBounded(t *testing.T) {
	m := NewManager()
	cur := time.Unix(1_000_000, 0)
	m.now = func() time.Time { return cur }

	countTerminal := func() int {
		m.mu.Lock()
		defer m.mu.Unlock()
		n := 0
		for _, rec := range m.executions {
			if IsTerminal(rec.status) {
				n++
			}
		}
		return n
	}

	// Count bound: settle well past the cap; the oldest settled records
	// must be evicted, newest retained.
	total := maxSettledExecutions + 50
	for i := 0; i < total; i++ {
		id := fmt.Sprintf("exec-%d", i)
		m.mu.Lock()
		m.executions[id] = &record{instanceID: "inst-1", status: StatusRunning}
		m.mu.Unlock()
		m.settle("inst-1", id, &Result{Status: StatusSuccess, ExecutionID: id})
		cur = cur.Add(time.Millisecond)
	}
	if got := countTerminal(); got > maxSettledExecutions {
		t.Fatalf("terminal records = %d, want <= %d", got, maxSettledExecutions)
	}
	m.mu.Lock()
	_, oldestPresent := m.executions["exec-0"]
	_, newestPresent := m.executions[fmt.Sprintf("exec-%d", total-1)]
	m.mu.Unlock()
	if oldestPresent {
		t.Error("oldest settled record should have been evicted by the count bound")
	}
	if !newestPresent {
		t.Error("newest settled record must be retained")
	}

	// A non-terminal record is never evicted, no matter how old.
	m.mu.Lock()
	m.executions["exec-live"] = &record{instanceID: "inst-2", status: StatusRunning}
	m.mu.Unlock()

	// Age bound: once retention lapses, a new settle sweeps the aged ones.
	cur = cur.Add(settledRetention + time.Minute)
	m.mu.Lock()
	m.executions["exec-final"] = &record{instanceID: "inst-1", status: StatusRunning}
	m.mu.Unlock()
	m.settle("inst-1", "exec-final", &Result{Status: StatusSuccess, ExecutionID: "exec-final"})

	if got := countTerminal(); got != 1 {
		t.Errorf("terminal records after retention lapse = %d, want just the fresh one", got)
	}
	m.mu.Lock()
	_, livePresent := m.executions["exec-live"]
	_, finalPresent := m.executions["exec-final"]
	m.mu.Unlock()
	if !livePresent {
		t.Error("a non-terminal record must never be evicted")
	}
	if !finalPresent {
		t.Error("the freshly settled record must be retained")
	}
}

// TestPollAnsweredUnknownByCurrentConn_SettlesAndFreesInstance pins issue
// #42's fix: a phantom execution (the execute_script wire call lost on a
// half-open connection, so the add-in never received it) previously kept the
// busy latch held with only auto-cancel -> grace escalation -> a HEALTHY
// instance falsely latched unrecoverable as the exit. The owning instance's
// current connection answering unknown-execution-id is authoritative: the
// record settles as error, the instance frees, and no unrecoverable latch is
// set.
func TestPollAnsweredUnknownByCurrentConn_SettlesAndFreesInstance(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		if method == "execute_script" {
			return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
		}
		// The add-in has no record of this execution — the wire answer for
		// a poll of a phantom (matches RequestDispatcher's own code string).
		return nil, &transport.RPCError{
			Code:    transport.ErrCodeInvalidParams,
			Message: "unknown execution_id",
			Data:    diag.New(diag.SeverityError, "unknown-execution-id", "mcp-bridge.core.dispatch", "unknown execution_id"),
		}
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "phantom", 50, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	// The poll surfaces the add-in's own answer to THIS call unchanged...
	_, pollDrec := m.PollExecution(context.Background(), start.ExecutionID, 500)
	if pollDrec == nil || pollDrec.Code != "unknown-execution-id" {
		t.Fatalf("got %+v, want the add-in's unknown-execution-id passed through", pollDrec)
	}

	// ...but the broker-side record must now be settled: the instance is
	// idle (not busy, not unrecoverable) and a NEW execute_script routes.
	if got := m.StatusForInstance("inst-1"); got != StatusIdle {
		t.Fatalf("StatusForInstance = %q, want idle after the lost execution settled", got)
	}

	// A later poll of the settled id returns the stored terminal result.
	settled, drec2 := m.PollExecution(context.Background(), start.ExecutionID, 500)
	if drec2 != nil {
		t.Fatalf("poll of settled record: %+v", drec2)
	}
	if settled.Status != StatusError || settled.ErrorDetail == nil || settled.ErrorDetail.Code != "execution-lost" {
		t.Errorf("settled = %+v, want error with execution-lost detail", settled)
	}
}

// TestUnknownAnswerFromDisplacedConn_DoesNotSettle pins the conn-identity
// guard on that settle (CONVENTIONS.md: the acting connection's identity
// travels with the action): a connection displaced by a re-register while
// its answer was in flight must not settle a run the live connection may
// genuinely be executing.
func TestUnknownAnswerFromDisplacedConn_DoesNotSettle(t *testing.T) {
	release := make(chan struct{})
	pollEntered := make(chan struct{})
	var pollEnteredOnce sync.Once
	_, connA := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		if method == "execute_script" {
			return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
		}
		// Signal the poll has genuinely reached this handler (PR review: a
		// fixed sleep made the race probabilistic -- the displacement could
		// land before the poll was even forwarded, passing vacuously), then
		// hold the answer until the test has displaced this conn.
		pollEnteredOnce.Do(func() { close(pollEntered) })
		<-release
		return nil, &transport.RPCError{
			Code:    transport.ErrCodeInvalidParams,
			Message: "unknown execution_id",
			Data:    diag.New(diag.SeverityError, "unknown-execution-id", "mcp-bridge.core.dispatch", "unknown execution_id"),
		}
	})
	m := NewManager()
	m.AttachInstance("inst-1", connA)

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	pollDone := make(chan struct{})
	go func() {
		defer close(pollDone)
		_, _ = m.PollExecution(context.Background(), start.ExecutionID, 2000)
	}()

	// Displace connA only once its poll is provably in flight (inside the
	// handler, answer held), then let the stale answer land -- deterministic,
	// no scheduler-timing dependence.
	<-pollEntered
	_, connB := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		return Result{Status: StatusRunning}, nil
	})
	m.AttachInstance("inst-1", connB)
	close(release)
	<-pollDone

	m.mu.Lock()
	rec := m.executions[start.ExecutionID]
	m.mu.Unlock()
	if rec == nil || IsTerminal(rec.status) {
		t.Fatalf("record = %+v; a stale connection's unknown answer must not settle it", rec)
	}
}

// TestGraceEscalationDeclinesWhenTheRedialBeatsTheTimer pins issue #47 in
// its DOMINANT timing, proved by the PR's independent review: the add-in's
// reconnect backoff starts at 1s while the grace period is 10s, so the
// realistic sequence is detach → cancel in the disconnected gap → redial ~1s
// later → grace fires at T+10s with a fresh, innocent connection attached.
// A fire-time "is anything attached" check (this fix's own first version)
// latches exactly that healthy redialed instance; the schedule-time capture
// declines, because the connection that was told to cancel (none — nil at
// schedule) is not the one attached now. The connected-but-unresponsive
// boundary (same connection from schedule through fire — escalation's whole
// job) is pinned by TestCancelExecutionEscalatesToUnrecoverableAfterGracePeriod
// and must not regress.
func TestGraceEscalationDeclinesWhenTheRedialBeatsTheTimer(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
	})
	m, fa := newManagerWithFakeClock()
	m.AttachInstance("inst-1", conn)

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "about to vanish", 50, 0, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	// The connection drops and its teardown runs (identity-guarded detach,
	// as serveAddIn does) before anything cancels.
	if !m.DetachInstance("inst-1", conn) {
		t.Fatal("test setup: detach should have applied to the current connection")
	}

	// The cancel lands in the disconnected gap: escalation is scheduled
	// (unconditionally, by design — see CancelExecution's comment) with a
	// NIL connection captured, and the wire forward fails fast.
	if _, cancelDrec := m.CancelExecution(context.Background(), start.ExecutionID); cancelDrec == nil || cancelDrec.Code != "instance-disconnected" {
		t.Fatalf("cancel of a disconnected instance's execution: %+v, want instance-disconnected", cancelDrec)
	}

	// The redial BEATS the timer — a fresh, healthy connection is attached
	// by the time the grace period lapses. This is the interleaving the
	// review's demonstration test proved the first fix latched on.
	_, conn2 := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusSuccess, ExecutionID: p["execution_id"].(string), Output: "alive"}, nil
	})
	m.AttachInstance("inst-1", conn2)

	fa.fireAll()

	// No false latch, and the record stays non-terminal (the #46
	// execution-lost path on the first post-redial poll is what resolves
	// it — the broker must not assert an outcome it doesn't know)...
	m.mu.Lock()
	stillOpen := m.executions[start.ExecutionID]
	latched := m.unrecoverable["inst-1"]
	m.mu.Unlock()
	if stillOpen == nil || IsTerminal(stillOpen.status) {
		t.Fatalf("record = %+v; a declined escalation must leave it non-terminal", stillOpen)
	}
	if latched {
		t.Fatal("a healthy redialed instance must not be latched unrecoverable by a grace timer scheduled against its predecessor")
	}

	// ...and the redialed instance is fully usable.
	res, drec2 := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "post-redial", 1000, 0, ScriptOptions{})
	if drec2 != nil {
		t.Fatalf("ExecuteScript after redial: %+v — the instance must not be bricked", drec2)
	}
	if res.Status != StatusSuccess || res.Output != "alive" {
		t.Errorf("res = %+v, want the redialed instance to execute normally", res)
	}
}

// TestGraceEscalationDeclinesWhenTheConnectionWasDisplaced pins the
// displaced-connection sibling: the cancel captured connection A, but a
// re-register displaced A with B before the grace period lapsed (A's
// identity-guarded teardown then declines to clear anything, so the OLD
// execution's busy latch survives displacement). Escalation must decline —
// A, the connection that was told to cancel, is gone — and the decline path
// is what frees that surviving busy latch, without touching a newer
// execution running on B.
func TestGraceEscalationDeclinesWhenTheConnectionWasDisplaced(t *testing.T) {
	blockCancel := make(chan struct{})
	t.Cleanup(func() { close(blockCancel) })
	_, connA := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		if method == "execute_script" {
			return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
		}
		<-blockCancel // the cancel wire call never completes on A
		return Result{Status: StatusCancelled, ExecutionID: p["execution_id"].(string)}, nil
	})
	m, fa := newManagerWithFakeClock()
	m.AttachInstance("inst-1", connA)

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "on the doomed conn", 50, 0, ScriptOptions{})
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	// Cancel while A is still current: the escalation captures A.
	cancelCtx, cancelCause := context.WithCancel(context.Background())
	t.Cleanup(cancelCause)
	cancelDone := make(chan struct{})
	go func() {
		_, _ = m.CancelExecution(cancelCtx, start.ExecutionID)
		close(cancelDone)
	}()
	deadline := time.Now().Add(2 * time.Second)
	for {
		fa.mu.Lock()
		n := len(fa.calls)
		fa.mu.Unlock()
		if n > 0 {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("CancelExecution never scheduled a grace-escalation timer")
		}
		time.Sleep(time.Millisecond)
	}

	// A re-register displaces A with B before the grace period lapses.
	_, connB := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusSuccess, ExecutionID: p["execution_id"].(string), Output: "from-B"}, nil
	})
	if displaced := m.AttachInstance("inst-1", connB); displaced != connA {
		t.Fatalf("attach of B should displace A, displaced %v", displaced)
	}

	fa.fireAll()
	cancelCause()
	<-cancelDone

	// No latch — the connection that was told to cancel is gone — and the
	// old execution's busy latch (which displacement left standing) is
	// freed by the decline: a fresh execute_script on B runs, not busy.
	m.mu.Lock()
	latched := m.unrecoverable["inst-1"]
	m.mu.Unlock()
	if latched {
		t.Fatal("a displaced connection's lapsed grace period must not latch the instance its replacement is serving")
	}
	res, drec2 := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "on the new conn", 1000, 0, ScriptOptions{})
	if drec2 != nil {
		t.Fatalf("ExecuteScript on the displacing connection: %+v", drec2)
	}
	if res.Status != StatusSuccess || res.Output != "from-B" {
		t.Errorf("res = %+v, want success from the displacing connection", res)
	}
}

// Issue #54: an execution that completed add-in-side with nobody polling used
// to hold the busy latch until the NEXT poll or execute_script, which then
// answered busy about a finished run. The broker now reconciles first.
func TestExecuteScriptReconcilesAnUnpolledCompletionInsteadOfAnsweringBusy(t *testing.T) {
	var polls int32
	fi, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		switch method {
		case "execute_script":
			if p["script"] == "slow" {
				return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
			}
			return Result{Status: StatusSuccess, ExecutionID: p["execution_id"].(string), Output: "second"}, nil
		case "poll_execution":
			atomic.AddInt32(&polls, 1)
			// The add-in finished the slow script in the meantime; a zero-wait poll says so.
			if p["timeout_ms"] != float64(0) {
				t.Errorf("reconciliation poll must not wait: timeout_ms = %v", p["timeout_ms"])
			}
			return Result{Status: StatusSuccess, ExecutionID: p["execution_id"].(string), Output: "first"}, nil
		}
		return nil, &transport.RPCError{Code: -32601, Message: "unexpected " + method}
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	first, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 100, 60000, ScriptOptions{})
	if drec != nil || first.Status != StatusRunning {
		t.Fatalf("first: %+v %+v", first, drec)
	}

	second, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "fast", 5000, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("second: %+v", drec)
	}
	if second.Status != StatusSuccess || second.Output != "second" {
		t.Fatalf("second = %+v, want the new script to have run (not busy)", second)
	}
	if second.ExecutionID == first.ExecutionID {
		t.Errorf("the second call must mint its own execution, got the first's id %q", second.ExecutionID)
	}
	if atomic.LoadInt32(&polls) != 1 {
		t.Errorf("expected exactly one reconciliation poll, got %d", polls)
	}
	if atomic.LoadInt32(&fi.requests) != 3 {
		t.Errorf("expected 3 wire requests (execute, reconcile poll, execute), got %d", fi.requests)
	}

	// The first run's result is retained and pollable, exactly as if the agent had polled it.
	res, drec := m.PollExecution(context.Background(), first.ExecutionID, 1000)
	if drec != nil || res.Status != StatusSuccess || res.Output != "first" {
		t.Fatalf("first run's settled result = %+v %+v", res, drec)
	}
}

func TestExecuteScriptStillAnswersBusyWhileTheAddInReportsRunning(t *testing.T) {
	fi, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	first, _ := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 100, 60000, ScriptOptions{})
	second, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "another", 100, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("second: %+v", drec)
	}
	if second.Status != StatusBusy || second.ExecutionID != first.ExecutionID {
		t.Fatalf("second = %+v, want busy pointing at %q", second, first.ExecutionID)
	}
	// execute, reconcile poll, and nothing else: the second script was never forwarded.
	if atomic.LoadInt32(&fi.requests) != 2 {
		t.Errorf("expected 2 wire requests, got %d", fi.requests)
	}
}

func TestReconcileBusyFreesTheLatchForStatusReporting(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		if method == "poll_execution" {
			return Result{Status: StatusSuccess, ExecutionID: p["execution_id"].(string)}, nil
		}
		return Result{Status: StatusPending, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)
	if _, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 100, 60000, ScriptOptions{}); drec != nil {
		t.Fatal(drec)
	}
	if got := m.StatusForInstance("inst-1"); got != StatusPending {
		t.Fatalf("before reconcile: %q", got)
	}

	m.ReconcileBusy(context.Background(), "inst-1")

	if got := m.StatusForInstance("inst-1"); got != StatusIdle {
		t.Errorf("after reconcile: %q, want idle", got)
	}
	// Idle instance: a no-op, no wire traffic implied.
	m.ReconcileBusy(context.Background(), "inst-1")
	m.ReconcileBusy(context.Background(), "inst-never-seen")
}

func TestReconcileBusyLeavesTheLatchWhenTheWireCallFails(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		if method == "poll_execution" {
			return nil, &transport.RPCError{Code: -32000, Message: "simulated add-in failure"}
		}
		return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)
	first, _ := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 100, 60000, ScriptOptions{})

	second, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "another", 100, 60000, ScriptOptions{})
	if drec != nil {
		t.Fatalf("a failed reconciliation must not surface as the caller's error: %+v", drec)
	}
	if second.Status != StatusBusy || second.ExecutionID != first.ExecutionID {
		t.Fatalf("second = %+v, want the honest busy answer", second)
	}
}
