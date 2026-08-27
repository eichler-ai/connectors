package discovery

import (
	"context"
	"encoding/json"
	"net"
	"testing"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/transport"
)

// fakeInstance simulates the add-in side of one instance's wire connection:
// a transport.Conn whose request handler is scriptable per test. Modeled on
// internal/execution/execution_test.go's own fakeInstance, for consistency.
type fakeInstance struct {
	conn *transport.Conn
}

func newFakeInstance(t *testing.T, handler transport.RequestHandler) (*fakeInstance, *transport.Conn) {
	t.Helper()
	brokerSide, addinSide := net.Pipe()
	brokerConn := transport.NewConn(brokerSide)
	addinConn := transport.NewConn(addinSide)

	addinConn.SetRequestHandler(handler)

	go brokerConn.Serve()
	go addinConn.Serve()
	t.Cleanup(func() {
		brokerConn.Close()
		addinConn.Close()
	})
	return &fakeInstance{conn: addinConn}, brokerConn
}

func handlerReturning(result any) transport.RequestHandler {
	return func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		return result, nil
	}
}

func TestListFunctionsRoutesToExplicitInstance(t *testing.T) {
	var gotMethod string
	var gotParams map[string]any
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		gotMethod = method
		json.Unmarshal(params, &gotParams)
		return map[string]any{"members": []any{}, "total_scoped": 0}, nil
	})
	r := NewRouter()
	r.AttachInstance("inst-1", conn)

	raw, drec := r.ListFunctions(context.Background(), "inst-1", map[string]any{"namespace": "Autodesk.Revit.DB"})
	if drec != nil {
		t.Fatalf("unexpected diag error: %+v", drec)
	}
	if gotMethod != "list_functions" {
		t.Errorf("wire method = %q, want list_functions", gotMethod)
	}
	if gotParams["namespace"] != "Autodesk.Revit.DB" {
		t.Errorf("params = %+v, want namespace forwarded", gotParams)
	}
	var out map[string]any
	if err := json.Unmarshal(raw, &out); err != nil {
		t.Fatalf("decoding result: %v", err)
	}
	if _, ok := out["members"]; !ok {
		t.Errorf("result missing members field: %+v", out)
	}
}

func TestSearchFunctionsRoutesToAnyInstanceDeterministically(t *testing.T) {
	var seenInstances []string

	_, connZ := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		seenInstances = append(seenInstances, "inst-z")
		return map[string]any{"results": []any{}, "total_matched": 0}, nil
	})
	_, connA := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		seenInstances = append(seenInstances, "inst-a")
		return map[string]any{"results": []any{}, "total_matched": 0}, nil
	})

	r := NewRouter()
	// Attach in non-sorted order to prove the pick is deterministic (sorted),
	// not map-iteration-order dependent.
	r.AttachInstance("inst-z", connZ)
	r.AttachInstance("inst-a", connA)

	_, drec := r.SearchFunctions(context.Background(), "", map[string]any{"query": "Delete"})
	if drec != nil {
		t.Fatalf("unexpected diag error: %+v", drec)
	}
	if len(seenInstances) != 1 || seenInstances[0] != "inst-a" {
		t.Errorf("seenInstances = %v, want exactly [inst-a] (lexicographically first)", seenInstances)
	}
}

func TestDescribeFunctionNoInstanceConnected(t *testing.T) {
	r := NewRouter()
	_, drec := r.DescribeFunction(context.Background(), "", map[string]any{"member": "Autodesk.Revit.DB.Document.Delete"})
	if drec == nil {
		t.Fatal("expected diag error when no instance is connected")
	}
	if drec.Code != "no_instance_connected" {
		t.Errorf("Code = %q, want no_instance_connected", drec.Code)
	}
	if drec.Source != "mcp-server.internal.discovery" {
		t.Errorf("Source = %q, want mcp-server.internal.discovery", drec.Source)
	}
}

func TestListFunctionsUnknownInstanceID(t *testing.T) {
	r := NewRouter()
	_, conn := newFakeInstance(t, handlerReturning(map[string]any{"members": []any{}}))
	r.AttachInstance("inst-1", conn)

	_, drec := r.ListFunctions(context.Background(), "ghost", map[string]any{})
	if drec == nil {
		t.Fatal("expected diag error for unknown instance_id")
	}
	if drec.Code != "instance_not_found" {
		t.Errorf("Code = %q, want instance_not_found", drec.Code)
	}
	if drec.Detail["instance_id"] != "ghost" {
		t.Errorf("Detail should name the instance_id, got %+v", drec.Detail)
	}
}

func TestDescribeFunctionRPCErrorPropagatesDiagnosticData(t *testing.T) {
	addinRecord := diag.New(diag.SeverityError, "member_not_found", "mcp-bridge.core.discovery", "no member matches \"Bogus.Member\"")
	_, conn := newFakeInstance(t, func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		return nil, &transport.RPCError{
			Code:    transport.ErrCodeInvalidParams,
			Message: "member not found",
			Data:    addinRecord,
		}
	})
	r := NewRouter()
	r.AttachInstance("inst-1", conn)

	_, drec := r.DescribeFunction(context.Background(), "inst-1", map[string]any{"member": "Bogus.Member"})
	if drec == nil {
		t.Fatal("expected diag error")
	}
	if drec.Code != "member_not_found" {
		t.Errorf("Code = %q, want the add-in's own code to pass through unwrapped", drec.Code)
	}
}

func TestListFunctionsWireFailurePropagates(t *testing.T) {
	_, conn := newFakeInstance(t, handlerReturning(map[string]any{"members": []any{}}))
	r := NewRouter()
	r.AttachInstance("inst-1", conn)
	conn.Close() // simulate a dead wire

	_, drec := r.ListFunctions(context.Background(), "inst-1", map[string]any{})
	if drec == nil {
		t.Fatal("expected diag error on wire failure")
	}
	if drec.Code != "wire_call_failed" {
		t.Errorf("Code = %q, want wire_call_failed", drec.Code)
	}
}

func TestDetachInstanceRemovesFromRouting(t *testing.T) {
	_, conn := newFakeInstance(t, handlerReturning(map[string]any{"members": []any{}}))
	r := NewRouter()
	r.AttachInstance("inst-1", conn)
	r.DetachInstance("inst-1")

	_, drec := r.ListFunctions(context.Background(), "inst-1", map[string]any{})
	if drec == nil || drec.Code != "instance_not_found" {
		t.Fatalf("got %+v, want instance_not_found after detach", drec)
	}

	_, drec2 := r.ListFunctions(context.Background(), "", map[string]any{})
	if drec2 == nil || drec2.Code != "no_instance_connected" {
		t.Fatalf("got %+v, want no_instance_connected after detach leaves the map empty", drec2)
	}
}

func TestSearchFunctionsForwardsResultUnmodified(t *testing.T) {
	// The router must pass the add-in's result JSON through unmodified —
	// including a field the router doesn't itself know about — rather than
	// decoding into a narrow struct and re-encoding, which would silently
	// drop unknown fields.
	_, conn := newFakeInstance(t, handlerReturning(map[string]any{
		"results":             []any{map[string]any{"member_id": "M:Foo", "score": 0.9}},
		"total_matched":       1,
		"an_unexpected_field": "should survive round trip",
	}))
	r := NewRouter()
	r.AttachInstance("inst-1", conn)

	raw, drec := r.SearchFunctions(context.Background(), "inst-1", map[string]any{"query": "Foo"})
	if drec != nil {
		t.Fatalf("unexpected diag error: %+v", drec)
	}
	var out map[string]any
	if err := json.Unmarshal(raw, &out); err != nil {
		t.Fatalf("decoding: %v", err)
	}
	if out["an_unexpected_field"] != "should survive round trip" {
		t.Errorf("out = %+v, want unknown field preserved", out)
	}
}
