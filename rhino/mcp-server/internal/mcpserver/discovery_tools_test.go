package mcpserver

import (
	"context"
	"encoding/json"
	"net"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/internal/servercore/semsearch/manager"
	"github.com/eichler-ai/connectors/internal/servercore/transport"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/discovery"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/registry"
)

// fakeConns is a discovery.Conns backed by one scripted in-memory plug-in conn.
type fakeConns struct {
	conn    *transport.Conn
	present bool
}

func (f *fakeConns) Conn(string) (*transport.Conn, bool) { return f.conn, f.present }
func (f *fakeConns) Connected() []string {
	if f.present {
		return []string{"inst-1"}
	}
	return nil
}

// discoveryRouterWithFake builds a Router whose single connected instance
// answers wire calls with handler; the registry knows it at Rhino 8.35.
func discoveryRouterWithFake(t *testing.T, present bool, handler func(method string, p map[string]any) (any, *transport.RPCError)) *discovery.Router {
	t.Helper()
	f := &fakeConns{present: present}
	if present {
		a, b := net.Pipe()
		server := transport.NewConn(a)
		plugin := transport.NewConn(b)
		plugin.SetRequestHandler(func(_ context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
			var p map[string]any
			json.Unmarshal(params, &p)
			return handler(method, p)
		})
		go server.Serve()
		go plugin.Serve()
		t.Cleanup(func() { server.Close(); plugin.Close() })
		f.conn = server
	}
	reg := registry.New()
	reg.Register(&registry.Instance{InstanceID: "inst-1", RhinoVersion: "8.35"}, 0, time.Now())
	return discovery.NewRouter(f, reg)
}

func discoveryClient(t *testing.T, r *discovery.Router) *mcp.ClientSession {
	return discoveryClientWith(t, r, nil)
}

func discoveryClientWith(t *testing.T, r *discovery.Router, search *manager.Manager) *mcp.ClientSession {
	t.Helper()
	server := mcp.NewServer(&mcp.Implementation{Name: "rhino-mcp-server-test", Version: "0.0.0"}, nil)
	RegisterDiscovery(server, r, search)
	ct, st := mcp.NewInMemoryTransports()
	ctx := context.Background()
	if _, err := server.Connect(ctx, st, nil); err != nil {
		t.Fatalf("server.Connect: %v", err)
	}
	client := mcp.NewClient(&mcp.Implementation{Name: "test-client", Version: "0.0.0"}, nil)
	cs, err := client.Connect(ctx, ct, nil)
	if err != nil {
		t.Fatalf("client.Connect: %v", err)
	}
	t.Cleanup(func() { cs.Close() })
	return cs
}

func callDiscovery[T any](t *testing.T, cs *mcp.ClientSession, name string, args map[string]any) (T, bool) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{Name: name, Arguments: args})
	if err != nil {
		t.Fatalf("CallTool %s: %v", name, err)
	}
	var out T
	sc, _ := json.Marshal(res.StructuredContent)
	if err := json.Unmarshal(sc, &out); err != nil {
		t.Fatalf("decode %s: %v", name, err)
	}
	return out, res.IsError
}

func TestListFunctionsTool_NamespacesTier_StampsRhinoVersion(t *testing.T) {
	r := discoveryRouterWithFake(t, true, func(method string, _ map[string]any) (any, *transport.RPCError) {
		if method != "list_functions" {
			t.Errorf("method = %q", method)
		}
		return map[string]any{
			"namespaces":   []any{map[string]any{"namespace": "Rhino.Geometry", "type_count": 900}},
			"next_cursor":  "50",
			"total_scoped": 60,
		}, nil
	})
	out, isErr := callDiscovery[ListFunctionsOut](t, discoveryClient(t, r), "list_functions", map[string]any{})
	if isErr {
		t.Fatalf("tool error: %+v", out.Error)
	}
	if len(out.Namespaces) != 1 || out.Namespaces[0].Namespace != "Rhino.Geometry" || out.Namespaces[0].TypeCount != 900 {
		t.Fatalf("namespaces = %+v", out.Namespaces)
	}
	if out.RhinoVersion != "8.35" {
		t.Fatalf("rhino_version = %q, want the registry's 8.35 stamped by the broker", out.RhinoVersion)
	}
}

func TestSearchFunctionsTool_FallbackRankerAndGuidance(t *testing.T) {
	r := discoveryRouterWithFake(t, true, func(method string, p map[string]any) (any, *transport.RPCError) {
		if method != "search_functions" {
			t.Errorf("method = %q", method)
		}
		return map[string]any{
			"results":       []any{map[string]any{"member_id": "M:Rhino.Geometry.Circle", "kind": "core", "name": "Circle", "signature": "Circle"}},
			"total_matched": 1,
		}, nil
	})
	// A manager is wired but its index is never built (no OnAttach), so search_functions falls back to
	// the plug-in ranker AND labels it keyword-fallback with a building notice.
	search := manager.New(r, nil, nil, func(string, ...any) {})
	out, isErr := callDiscovery[SearchFunctionsOut](t, discoveryClientWith(t, r, search), "search_functions", map[string]any{"query": "add a circle to the document"})
	if isErr {
		t.Fatalf("tool error: %+v", out.Error)
	}
	if len(out.Results) != 1 || out.Results[0].Name != "Circle" {
		t.Fatalf("results = %+v", out.Results)
	}
	if out.Ranker != rankerKeywordFallback {
		t.Fatalf("ranker = %q, want keyword-fallback (no broker index wired)", out.Ranker)
	}
	if out.Guidance == "" {
		t.Fatal("expected guidance on a fallback response")
	}
	if len(out.Notices) == 0 {
		t.Fatal("expected a search-index-building notice on the fallback path")
	}
}

func TestDescribeFunctionTool_ForwardsMemberAndStampsVersion(t *testing.T) {
	r := discoveryRouterWithFake(t, true, func(method string, p map[string]any) (any, *transport.RPCError) {
		if method != "describe_function" || p["member"] != "Rhino.Geometry.Sphere.Radius" {
			t.Errorf("method=%q params=%+v", method, p)
		}
		return map[string]any{"member_id": "P:Rhino.Geometry.Sphere.Radius", "summary": "the radius"}, nil
	})
	out, isErr := callDiscovery[DescribeFunctionOut](t, discoveryClient(t, r), "describe_function", map[string]any{"member": "Rhino.Geometry.Sphere.Radius"})
	if isErr {
		t.Fatalf("tool error: %+v", out.Error)
	}
	if out.Result["summary"] != "the radius" || out.RhinoVersion != "8.35" {
		t.Fatalf("out = %+v", out)
	}
}

func TestDescribeFunctionTool_NeitherMemberNorIDIsToolError(t *testing.T) {
	r := discoveryRouterWithFake(t, true, func(string, map[string]any) (any, *transport.RPCError) {
		t.Error("wire should not be called when both member and member_id are empty")
		return nil, nil
	})
	out, isErr := callDiscovery[DescribeFunctionOut](t, discoveryClient(t, r), "describe_function", map[string]any{})
	if !isErr || out.Error == nil || out.Error.Code != "missing-required-param" {
		t.Fatalf("out = %+v (isErr=%v)", out, isErr)
	}
}

func TestListFunctionsTool_NoInstanceIsToolError(t *testing.T) {
	r := discoveryRouterWithFake(t, false, nil)
	out, isErr := callDiscovery[ListFunctionsOut](t, discoveryClient(t, r), "list_functions", map[string]any{})
	if !isErr || out.Error == nil || out.Error.Code != "no-instance-connected" {
		t.Fatalf("out = %+v (isErr=%v)", out, isErr)
	}
}

func TestDiscoveryToolsAreRegistered(t *testing.T) {
	r := discoveryRouterWithFake(t, false, nil)
	cs := discoveryClient(t, r)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.ListTools(ctx, nil)
	if err != nil {
		t.Fatalf("ListTools: %v", err)
	}
	want := map[string]bool{"list_functions": false, "search_functions": false, "describe_function": false}
	for _, tool := range res.Tools {
		if _, ok := want[tool.Name]; ok {
			want[tool.Name] = true
		}
	}
	for name, found := range want {
		if !found {
			t.Errorf("tool %q not registered", name)
		}
	}
}
