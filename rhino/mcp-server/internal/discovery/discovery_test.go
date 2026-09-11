package discovery

import (
	"context"
	"encoding/json"
	"net"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/internal/servercore/transport"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/registry"
)

// fakeConns is a configurable connection source: a set of instance ids, each
// mapped to a scripted in-memory plug-in conn.
type fakeConns struct {
	conns map[string]*transport.Conn
	last  struct {
		method string
		params map[string]any
	}
}

func (f *fakeConns) Conn(id string) (*transport.Conn, bool) { c, ok := f.conns[id]; return c, ok }
func (f *fakeConns) Connected() []string {
	ids := make([]string, 0, len(f.conns))
	for id := range f.conns {
		ids = append(ids, id)
	}
	return ids
}

// add wires a scripted plug-in conn for id; answer handles its wire requests.
func (f *fakeConns) add(t *testing.T, id string, answer func(method string, p map[string]any) (any, *transport.RPCError)) {
	t.Helper()
	a, b := net.Pipe()
	server := transport.NewConn(a)
	plugin := transport.NewConn(b)
	plugin.SetRequestHandler(func(_ context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		f.last.method, f.last.params = method, p
		return answer(method, p)
	})
	go server.Serve()
	go plugin.Serve()
	t.Cleanup(func() { server.Close(); plugin.Close() })
	if f.conns == nil {
		f.conns = map[string]*transport.Conn{}
	}
	f.conns[id] = server
}

func regWith(t *testing.T, versions map[string]string) *registry.Registry {
	t.Helper()
	r := registry.New()
	now := time.Now()
	for id, v := range versions {
		r.Register(&registry.Instance{InstanceID: id, RhinoVersion: v}, 0, now)
	}
	return r
}

func TestNewRouterPanicsOnNilRegistry(t *testing.T) {
	defer func() {
		if recover() == nil {
			t.Fatal("expected panic on nil registry")
		}
	}()
	NewRouter(&fakeConns{}, nil)
}

func TestResolveExplicitInstance(t *testing.T) {
	f := &fakeConns{}
	f.add(t, "i1", func(string, map[string]any) (any, *transport.RPCError) { return map[string]any{"ok": true}, nil })
	r := NewRouter(f, regWith(t, map[string]string{"i1": "8.35"}))

	raw, ver, drec := r.ListFunctions(context.Background(), "i1", map[string]any{})
	if drec != nil {
		t.Fatalf("drec = %s", drec.Message)
	}
	if ver != "8.35" {
		t.Fatalf("version = %q, want the registry's 8.35", ver)
	}
	if f.last.method != "list_functions" {
		t.Fatalf("method = %q", f.last.method)
	}
	if string(raw) == "" {
		t.Fatal("empty raw result")
	}
}

func TestExplicitInstanceNotConnected(t *testing.T) {
	r := NewRouter(&fakeConns{}, registry.New())
	_, _, drec := r.ListFunctions(context.Background(), "nope", map[string]any{})
	if drec == nil || drec.Code != "instance-not-found" {
		t.Fatalf("drec = %+v", drec)
	}
}

func TestOmittedNoneConnected(t *testing.T) {
	r := NewRouter(&fakeConns{}, registry.New())
	_, _, drec := r.ListFunctions(context.Background(), "", map[string]any{})
	if drec == nil || drec.Code != "no-instance-connected" {
		t.Fatalf("drec = %+v", drec)
	}
}

func TestOmittedSingleInstanceIsPicked(t *testing.T) {
	f := &fakeConns{}
	f.add(t, "only", func(string, map[string]any) (any, *transport.RPCError) { return map[string]any{}, nil })
	r := NewRouter(f, regWith(t, map[string]string{"only": "8.35"}))
	_, ver, drec := r.DescribeFunction(context.Background(), "", map[string]any{"member": "X"})
	if drec != nil || ver != "8.35" {
		t.Fatalf("ver=%q drec=%+v", ver, drec)
	}
}

func TestOmittedMultipleSameVersionPicksDeterministically(t *testing.T) {
	f := &fakeConns{}
	f.add(t, "b", func(string, map[string]any) (any, *transport.RPCError) { return map[string]any{"who": "b"}, nil })
	f.add(t, "a", func(string, map[string]any) (any, *transport.RPCError) { return map[string]any{"who": "a"}, nil })
	r := NewRouter(f, regWith(t, map[string]string{"a": "8.35", "b": "8.35"}))
	// Same version across all connected: not ambiguous; picks the lexically-first id ("a").
	raw, _, drec := r.ListFunctions(context.Background(), "", map[string]any{})
	if drec != nil {
		t.Fatalf("drec = %+v", drec)
	}
	var got map[string]string
	json.Unmarshal(raw, &got)
	if got["who"] != "a" {
		t.Fatalf("picked %q, want the sorted-first 'a'", got["who"])
	}
}

func TestOmittedMultipleDifferentVersionsIsAmbiguous(t *testing.T) {
	f := &fakeConns{}
	f.add(t, "a", func(string, map[string]any) (any, *transport.RPCError) { return map[string]any{}, nil })
	f.add(t, "b", func(string, map[string]any) (any, *transport.RPCError) { return map[string]any{}, nil })
	r := NewRouter(f, regWith(t, map[string]string{"a": "8.35", "b": "8.40"}))
	_, _, drec := r.SearchFunctions(context.Background(), "", map[string]any{"query": "x"})
	if drec == nil || drec.Code != "ambiguous-instance-version" {
		t.Fatalf("drec = %+v", drec)
	}
	cands, _ := drec.Detail["candidates"].([]map[string]string)
	if len(cands) != 2 {
		t.Fatalf("candidates = %+v", drec.Detail["candidates"])
	}
}

func TestUnknownVersionSentinelWhenNotInRegistry(t *testing.T) {
	f := &fakeConns{}
	f.add(t, "a", func(string, map[string]any) (any, *transport.RPCError) { return map[string]any{}, nil })
	f.add(t, "b", func(string, map[string]any) (any, *transport.RPCError) { return map[string]any{}, nil })
	// "a" has a version; "b" is connected but absent from the registry -> "unknown",
	// which differs from "8.35" so the call is ambiguous and lists the sentinel.
	r := NewRouter(f, regWith(t, map[string]string{"a": "8.35"}))
	_, _, drec := r.ListFunctions(context.Background(), "", map[string]any{})
	if drec == nil || drec.Code != "ambiguous-instance-version" {
		t.Fatalf("drec = %+v", drec)
	}
	cands, _ := drec.Detail["candidates"].([]map[string]string)
	var sawUnknown bool
	for _, c := range cands {
		if c["rhino_version"] == unknownRhinoVersion {
			sawUnknown = true
		}
	}
	if !sawUnknown {
		t.Fatalf("expected the 'unknown' sentinel in candidates, got %+v", cands)
	}
}

func TestDumpMembersForwardsOffsetAndLimit(t *testing.T) {
	f := &fakeConns{}
	f.add(t, "i1", func(_ string, p map[string]any) (any, *transport.RPCError) {
		return map[string]any{"members": []any{}}, nil
	})
	r := NewRouter(f, regWith(t, map[string]string{"i1": "8.35"}))
	_, _, drec := r.DumpMembers(context.Background(), "i1", 100, 500)
	if drec != nil {
		t.Fatalf("drec = %+v", drec)
	}
	if f.last.method != "dump_members" || f.last.params["offset"].(float64) != 100 || f.last.params["limit"].(float64) != 500 {
		t.Fatalf("params = %+v", f.last.params)
	}
}

func TestBridgeErrorIsForwarded(t *testing.T) {
	f := &fakeConns{}
	f.add(t, "i1", func(string, map[string]any) (any, *transport.RPCError) {
		return nil, &transport.RPCError{Message: "boom"}
	})
	r := NewRouter(f, regWith(t, map[string]string{"i1": "8.35"}))
	_, _, drec := r.DescribeFunction(context.Background(), "i1", map[string]any{"member": "X"})
	if drec == nil || drec.Code != "bridge-error" {
		t.Fatalf("drec = %+v", drec)
	}
}

// dropConns reports one instance in Connected() but returns no conn from Conn(),
// simulating the instance dropping in the window between the two calls
// (discovery.go's resolveConn documents handling this; F5a of #295 review — it had no coverage).
type dropConns struct{ id string }

func (d *dropConns) Conn(string) (*transport.Conn, bool) { return nil, false }
func (d *dropConns) Connected() []string                 { return []string{d.id} }

func TestResolveHandlesInstanceDroppedBetweenConnectedAndConn(t *testing.T) {
	r := NewRouter(&dropConns{id: "inst-gone"}, registry.New())
	_, _, drec := r.ResolveInstance("") // omitted -> Connected() lists it, Conn() then misses
	if drec == nil {
		t.Fatal("expected an error when the only connected instance drops mid-resolve, got nil")
	}
	if drec.Code != "no-instance-connected" {
		t.Fatalf("code = %q, want no-instance-connected", drec.Code)
	}
}
