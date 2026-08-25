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
