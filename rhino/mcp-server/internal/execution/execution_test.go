package execution

import (
	"context"
	"encoding/json"
	"net"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/internal/servercore/transport"
)

// fakeConns serves one scripted plug-in over an in-memory pipe.
type fakeConns struct {
	conn    *transport.Conn
	present bool
	// handler answers requests; params are echoed for assertions.
	last struct {
		method string
		params map[string]any
	}
}

func (f *fakeConns) Conn(string) (*transport.Conn, bool) { return f.conn, f.present }
func (f *fakeConns) Connected() []string {
	if f.present {
		return []string{"inst"}
	}
	return nil
}

func newFake(t *testing.T, answer func(method string, params map[string]any) (any, *transport.RPCError)) *fakeConns {
	t.Helper()
	a, b := net.Pipe()
	server := transport.NewConn(a)
	plugin := transport.NewConn(b)
	f := &fakeConns{conn: server, present: true}
	plugin.SetRequestHandler(func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		f.last.method, f.last.params = method, p
		return answer(method, p)
	})
	go server.Serve()
	go plugin.Serve()
	t.Cleanup(func() { server.Close(); plugin.Close() })
	return f
}

func TestExecuteScriptMintsANamespacedIdAndForwardsEveryParam(t *testing.T) {
	f := newFake(t, func(method string, p map[string]any) (any, *transport.RPCError) {
		return map[string]any{"status": "success", "execution_id": p["execution_id"], "return_value": "42"}, nil
	})
	r := NewRouter(f, "srvA")
	res, drec := r.ExecuteScript(context.Background(), "inst", "return 42;", Options{Language: "csharp", DocumentID: "doc-1", TimeoutMs: 1000, MaxDurationMs: 5000, ConfirmLifecycleActions: true, Label: "lbl"})
	if drec != nil {
		t.Fatal(drec.Message)
	}
	if res.Status != "success" || res.ReturnValue != "42" || !strings.HasPrefix(res.ExecutionID, "exec-srvA-") {
		t.Fatalf("res = %+v", res)
	}
	p := f.last.params
	if p["language"] != "csharp" || p["document_id"] != "doc-1" || p["confirm_lifecycle_actions"] != true || p["label"] != "lbl" || p["script"] != "return 42;" {
		t.Fatalf("params = %v", p)
	}
	if p["timeout_ms"].(float64) != 1000 || p["max_duration_ms"].(float64) != 5000 {
		t.Fatalf("timeouts = %v", p)
	}
}

func TestPollAndCancelRouteToTheOwningInstance(t *testing.T) {
	f := newFake(t, func(method string, p map[string]any) (any, *transport.RPCError) {
		return map[string]any{"status": map[string]string{"execute_script": "running", "poll_execution": "success", "cancel_execution": "cancelled"}[method], "execution_id": p["execution_id"]}, nil
	})
	r := NewRouter(f, "s")
	res, _ := r.ExecuteScript(context.Background(), "inst", "x", Options{Language: "csharp", TimeoutMs: 10})
	polled, drec := r.PollExecution(context.Background(), res.ExecutionID, 10)
	if drec != nil || polled.Status != "success" || f.last.params["execution_id"] != res.ExecutionID {
		t.Fatalf("poll: %+v %v", polled, drec)
	}
	cancelled, drec := r.CancelExecution(context.Background(), res.ExecutionID)
	if drec != nil || cancelled.Status != "cancelled" {
		t.Fatalf("cancel: %+v %v", cancelled, drec)
	}
}

func TestUnknownAndDisconnected(t *testing.T) {
	f := newFake(t, func(string, map[string]any) (any, *transport.RPCError) {
		return map[string]any{"status": "running"}, nil
	})
	r := NewRouter(f, "s")
	res, _ := r.ExecuteScript(context.Background(), "inst", "x", Options{Language: "csharp", TimeoutMs: 10})
	f.present = false
	if _, drec := r.PollExecution(context.Background(), res.ExecutionID, 0); drec == nil || drec.Code != "instance-disconnected" {
		t.Fatalf("got %v", drec)
	}
	if _, drec := r.ExecuteScript(context.Background(), "inst", "x", Options{Language: "csharp"}); drec == nil || drec.Code != "instance-not-found" {
		t.Fatalf("got %v", drec)
	}
}

func TestAnIdThisServerDidNotMintIsFoundByAskingTheInstances(t *testing.T) {
	// Another server's execution (or one from before a restart): the plug-in owns the record.
	f := newFake(t, func(method string, p map[string]any) (any, *transport.RPCError) {
		if p["execution_id"] == "exec-other-1" {
			return map[string]any{"status": "success", "execution_id": "exec-other-1", "return_value": "x"}, nil
		}
		return nil, &transport.RPCError{Code: -32602, Message: "unknown", Data: diagRecord("unknown-execution-id")}
	})
	r := NewRouter(f, "s")
	res, drec := r.PollExecution(context.Background(), "exec-other-1", 0)
	if drec != nil || res.Status != "success" {
		t.Fatalf("got %+v %v", res, drec)
	}
	if _, drec := r.PollExecution(context.Background(), "exec-nobody", 0); drec == nil || drec.Code != "unknown-execution-id" {
		t.Fatalf("got %v", drec)
	}
}

func TestPluginRpcErrorRecordPassesThrough(t *testing.T) {
	f := newFake(t, func(string, map[string]any) (any, *transport.RPCError) {
		return nil, &transport.RPCError{Code: -32602, Message: "no", Data: diagRecord("document-not-found")}
	})
	r := NewRouter(f, "s")
	_, drec := r.ExecuteScript(context.Background(), "inst", "x", Options{Language: "csharp", TimeoutMs: 10})
	if drec == nil || drec.Code != "document-not-found" {
		t.Fatalf("got %v", drec)
	}
}

func TestRouteMemoryIsBounded(t *testing.T) {
	f := newFake(t, func(_ string, p map[string]any) (any, *transport.RPCError) {
		return map[string]any{"status": "running", "execution_id": p["execution_id"]}, nil
	})
	r := NewRouter(f, "s")
	var first string
	for i := 0; i < maxRoutes+5; i++ {
		res, _ := r.ExecuteScript(context.Background(), "inst", "x", Options{Language: "csharp", TimeoutMs: 10})
		if i == 0 {
			first = res.ExecutionID
		}
	}
	if len(r.routes) != maxRoutes {
		t.Fatalf("routes = %d", len(r.routes))
	}
	if _, ok := r.routes[first]; ok {
		t.Fatal("the oldest route should have been evicted")
	}
	// Age-based eviction too.
	now := time.Now()
	r.now = func() time.Time { return now.Add(routeMaxAge + time.Second) }
	res, _ := r.ExecuteScript(context.Background(), "inst", "x", Options{Language: "csharp", TimeoutMs: 10})
	if len(r.routes) != 1 || r.routes[res.ExecutionID].instanceID != "inst" {
		t.Fatalf("after ageing, routes = %d", len(r.routes))
	}
}
