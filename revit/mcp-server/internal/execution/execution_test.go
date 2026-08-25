package execution

import (
	"context"
	"encoding/json"
	"net"
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

	res, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "return 42;", 5000, 60000)
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

	res, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "sleep forever", 100, 60000)
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
		res, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "long script", 60000, 600000)
		done <- outcome{res, drec}
	}()

	// Give the first call time to register itself as the active execution
	// for inst-1 before we issue the second one.
	time.Sleep(100 * time.Millisecond)

	res2, drec2 := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "second script", 5000, 60000)
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
	_, drec := m.ExecuteScript(context.Background(), "ghost", "doc-1", "1+1", 1000, 60000)
	if drec == nil {
		t.Fatal("expected diag error for unknown instance")
	}
	if drec.Code != "instance_not_found" {
		t.Errorf("Code = %q, want instance_not_found", drec.Code)
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
	if drec.Code != "unknown_execution_id" {
		t.Errorf("Code = %q, want unknown_execution_id", drec.Code)
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

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 60000)
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
	res, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "next", 50, 60000)
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

	res, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "fast", 5000, 60000)
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

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "loop forever", 50, 600000)
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
	res2, drec2 := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "fresh", 50, 60000)
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
	if drec == nil || drec.Code != "unknown_execution_id" {
		t.Fatalf("got %+v, want unknown_execution_id", drec)
	}
}

func TestWireErrorPropagatesDiagnosticData(t *testing.T) {
	addinRecord := diag.New(diag.SeverityError, "revit_api_exception", "mcp-bridge.core.execution", "System.InvalidOperationException: document is not active")
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		return nil, &transport.RPCError{
			Code:    transport.ErrCodeInternalError,
			Message: "script threw",
			Data:    addinRecord,
		}
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	_, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "boom", 1000, 60000)
	if drec == nil {
		t.Fatal("expected diag error")
	}
	if drec.Code != "revit_api_exception" {
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

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 60000)
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	m.DetachInstance("inst-1")

	_, drec2 := m.PollExecution(context.Background(), start.ExecutionID, 1000)
	if drec2 == nil || drec2.Code != "instance_disconnected" {
		t.Fatalf("got %+v, want instance_disconnected", drec2)
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

	_, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 60000)
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	// The add-in reconnects: its old connection is detached...
	m.DetachInstance("inst-1")

	// ...and a fresh execute_script against the same instance_id must
	// succeed, not report busy against the now-orphaned execution.
	_, conn2 := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusSuccess, ExecutionID: p["execution_id"].(string)}, nil
	})
	m.AttachInstance("inst-1", conn2)

	res, drec2 := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "retry", 1000, 60000)
	if drec2 != nil {
		t.Fatalf("ExecuteScript after reconnect: %+v, want success (not wedged busy)", drec2)
	}
	if res.Status != StatusSuccess {
		t.Errorf("Status = %q, want success — reconnect must free the instance", res.Status)
	}
}

// TestForwardExistingSettlesOnWireError is a regression test: a failed wire
// round trip (not a clean instance-disconnect, which has its own path) must
// still settle the execution to a terminal state, or it stays non-terminal
// forever, keeping the instance permanently marked busy.
func TestForwardExistingSettlesOnWireError(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 60000)
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
	if rec == nil || !IsTerminal(rec.status) {
		t.Fatalf("execution status = %+v, want terminal after the wire failure", rec)
	}

	// A fresh execute_script against the same instance must not report busy
	// anymore — the instance is free again.
	_, conn2 := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusSuccess, ExecutionID: p["execution_id"].(string)}, nil
	})
	m.AttachInstance("inst-1", conn2)
	res3, drec3 := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "retry", 1000, 60000)
	if drec3 != nil {
		t.Fatalf("ExecuteScript after wire failure: %+v, want it to succeed (not busy)", drec3)
	}
	if res3.Status != StatusSuccess {
		t.Errorf("Status = %q, want success", res3.Status)
	}
}

// TestUnrecoverableLatchesInstance is a regression test: per PRD §06, once
// an execution settles to "unrecoverable" the whole instance must stay
// rejected for execute_script until a Revit restart — not just free up
// activeByInstance like any other terminal status, which would forward a
// new script to a Revit instance already known to be wedged.
func TestUnrecoverableLatchesInstance(t *testing.T) {
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return Result{Status: StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
	})
	m := NewManager()
	m.AttachInstance("inst-1", conn)

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 60000)
	if drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	// Simulate the add-in's cancellation grace period lapsing.
	m.settle("inst-1", start.ExecutionID, &Result{Status: StatusUnrecoverable, ExecutionID: start.ExecutionID})

	_, drec2 := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "another", 1000, 60000)
	if drec2 == nil || drec2.Code != "instance_unrecoverable" {
		t.Fatalf("got %+v, want instance_unrecoverable", drec2)
	}

	// A restart (DetachInstance, then a fresh AttachInstance under a new
	// instance_id in practice — modeled here as the same ID reconnecting,
	// which is the worst case for latch cleanup) must clear the latch.
	m.DetachInstance("inst-1")
	m.AttachInstance("inst-1", conn)
	_, drec3 := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "after restart", 1000, 60000)
	if drec3 != nil && drec3.Code == "instance_unrecoverable" {
		t.Errorf("instance still latched unrecoverable after DetachInstance, want the latch cleared")
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

	start, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 60000)
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

	_, drec := m.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 100 /* maxDurationMs */)
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
