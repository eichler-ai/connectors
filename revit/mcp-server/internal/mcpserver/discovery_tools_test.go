package mcpserver

import (
	"context"
	"encoding/json"
	"net"
	"strings"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/discovery"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/registry"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/semsearch/manager"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/transport"
)

// connectDiscoveryClient wires an in-process MCP client to a server that has
// the discovery tools registered, using the SDK's in-memory transport pair —
// mirrors tools_test.go's connectClient.
// connectDiscoveryClient wires the discovery tools over in-memory MCP
// transports; search is the broker-side index (nil = add-in ranker only).
func connectDiscoveryClient(t *testing.T, r *discovery.Router, search *manager.Manager) *mcp.ClientSession {
	t.Helper()
	server := mcp.NewServer(&mcp.Implementation{Name: "revit-mcp-server-test", Version: "0.0.0"}, nil)
	RegisterDiscovery(server, r, search)

	clientTransport, serverTransport := mcp.NewInMemoryTransports()

	ctx := context.Background()
	if _, err := server.Connect(ctx, serverTransport, nil); err != nil {
		t.Fatalf("server.Connect: %v", err)
	}

	client := mcp.NewClient(&mcp.Implementation{Name: "test-client", Version: "0.0.0"}, nil)
	cs, err := client.Connect(ctx, clientTransport, nil)
	if err != nil {
		t.Fatalf("client.Connect: %v", err)
	}
	t.Cleanup(func() { cs.Close() })
	return cs
}

func attachFakeDiscoveryInstance(t *testing.T, r *discovery.Router, instanceID string, handler transport.RequestHandler) {
	t.Helper()
	brokerSide, addinSide := net.Pipe()
	brokerConn := transport.NewConn(brokerSide)
	addinConn := transport.NewConn(addinSide)
	addinConn.SetRequestHandler(handler)
	go brokerConn.Serve()
	go addinConn.Serve()
	t.Cleanup(func() { brokerConn.Close(); addinConn.Close() })
	r.AttachInstance(instanceID, brokerConn)
}

// list_functions is a strict one-level-at-a-time tree (PRD §08 addendum):
// no args -> namespaces, +namespace -> types, +namespace+type -> members.
// Each of the three tests below exercises one tier's wire shape.

func TestListFunctionsToolSuccess_NamespacesTier(t *testing.T) {
	r := discovery.NewRouter(registry.New())
	attachFakeDiscoveryInstance(t, r, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		if method != "list_functions" {
			t.Errorf("method = %q, want list_functions", method)
		}
		return map[string]any{
			"namespaces": []any{
				map[string]any{"namespace": "Autodesk.Revit.DB", "type_count": 1234},
			},
			"next_cursor":  "50",
			"total_scoped": 60,
		}, nil
	})
	cs := connectDiscoveryClient(t, r, nil)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{Name: "list_functions", Arguments: map[string]any{}})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	if res.IsError {
		t.Fatalf("unexpected tool error: %+v", res.Content)
	}

	var out ListFunctionsOut
	sc, _ := json.Marshal(res.StructuredContent)
	if err := json.Unmarshal(sc, &out); err != nil {
		t.Fatalf("decoding structured content: %v", err)
	}
	if len(out.Namespaces) != 1 || out.Namespaces[0].Namespace != "Autodesk.Revit.DB" || out.Namespaces[0].TypeCount != 1234 {
		t.Errorf("out.Namespaces = %+v", out.Namespaces)
	}
	if out.NextCursor != "50" || out.TotalScoped != 60 {
		t.Errorf("out = %+v", out)
	}
}

func TestListFunctionsToolSuccess_TypesTier(t *testing.T) {
	r := discovery.NewRouter(registry.New())
	attachFakeDiscoveryInstance(t, r, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		if method != "list_functions" {
			t.Errorf("method = %q, want list_functions", method)
		}
		return map[string]any{
			"namespace":    "Autodesk.Revit.DB",
			"types":        "Wall, Floor, Document",
			"next_cursor":  "50",
			"total_scoped": 1234,
		}, nil
	})
	cs := connectDiscoveryClient(t, r, nil)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name:      "list_functions",
		Arguments: map[string]any{"namespace": "Autodesk.Revit.DB"},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	if res.IsError {
		t.Fatalf("unexpected tool error: %+v", res.Content)
	}

	var out ListFunctionsOut
	sc, _ := json.Marshal(res.StructuredContent)
	if err := json.Unmarshal(sc, &out); err != nil {
		t.Fatalf("decoding structured content: %v", err)
	}
	if out.Namespace != "Autodesk.Revit.DB" || out.Types != "Wall, Floor, Document" {
		t.Errorf("out = %+v", out)
	}
	if out.NextCursor != "50" || out.TotalScoped != 1234 {
		t.Errorf("out = %+v", out)
	}
}

func TestListFunctionsToolSuccess_MembersTier(t *testing.T) {
	r := discovery.NewRouter(registry.New())
	attachFakeDiscoveryInstance(t, r, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		if method != "list_functions" {
			t.Errorf("method = %q, want list_functions", method)
		}
		return map[string]any{
			"namespace":    "Autodesk.Revit.DB",
			"type":         "Document",
			"members":      "Delete, Create",
			"total_scoped": 2,
		}, nil
	})
	cs := connectDiscoveryClient(t, r, nil)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name:      "list_functions",
		Arguments: map[string]any{"namespace": "Autodesk.Revit.DB", "type_name": "Autodesk.Revit.DB.Document"},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	if res.IsError {
		t.Fatalf("unexpected tool error: %+v", res.Content)
	}

	var out ListFunctionsOut
	sc, _ := json.Marshal(res.StructuredContent)
	if err := json.Unmarshal(sc, &out); err != nil {
		t.Fatalf("decoding structured content: %v", err)
	}
	if out.Namespace != "Autodesk.Revit.DB" || out.Type != "Document" || out.Members != "Delete, Create" {
		t.Errorf("out = %+v", out)
	}
	if out.TotalScoped != 2 {
		t.Errorf("out = %+v", out)
	}
}

func TestSearchFunctionsToolSuccess(t *testing.T) {
	r := discovery.NewRouter(registry.New())
	attachFakeDiscoveryInstance(t, r, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		if method != "search_functions" {
			t.Errorf("method = %q, want search_functions", method)
		}
		var p map[string]any
		json.Unmarshal(params, &p)
		if p["query"] != "Delete" {
			t.Errorf("query = %v, want Delete", p["query"])
		}
		return map[string]any{
			"results": []any{
				map[string]any{
					"member_id": "M:Autodesk.Revit.DB.Document.Delete(Autodesk.Revit.DB.ElementId)",
					"kind":      "Method",
					"name":      "Delete",
					"signature": "ICollection<ElementId> Delete(ElementId elementId)",
					"score":     0.95,
				},
			},
			"total_matched": 57,
		}, nil
	})
	cs := connectDiscoveryClient(t, r, nil)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name:      "search_functions",
		Arguments: map[string]any{"query": "Delete"},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	if res.IsError {
		t.Fatalf("unexpected tool error: %+v", res.Content)
	}
	var out SearchFunctionsOut
	sc, _ := json.Marshal(res.StructuredContent)
	json.Unmarshal(sc, &out)
	if len(out.Results) != 1 || out.Results[0].Score != 0.95 {
		t.Errorf("out.Results = %+v", out.Results)
	}
	if out.TotalMatched != 57 {
		t.Errorf("out.TotalMatched = %d, want 57", out.TotalMatched)
	}
	// total_matched 57 is well past searchManyResults (50), so this exercises the
	// "narrow it" branch: many matched, the wanted member may rank below the page,
	// so the hint steers toward narrowing -- never the empty-result wording.
	if out.Guidance == "" {
		t.Error("out.Guidance is empty; want a hint on every search response")
	}
	if strings.Contains(out.Guidance, "No members matched") {
		t.Errorf("out.Guidance used the empty-result wording for a non-empty result set: %q", out.Guidance)
	}
	if !strings.Contains(out.Guidance, "Narrow") {
		t.Errorf("out.Guidance = %q, want the narrow-it wording for a broad match set", out.Guidance)
	}
}

// TestSearchFunctionsToolWorkableSetGuidance pins the middle branch: a small,
// non-empty match set gets the reworded-retry nudge (the top hit can still be
// wrong), not the "narrow it" wording reserved for broad sets.
func TestSearchFunctionsToolWorkableSetGuidance(t *testing.T) {
	r := discovery.NewRouter(registry.New())
	attachFakeDiscoveryInstance(t, r, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		return map[string]any{
			"results": []any{
				map[string]any{"member_id": "M:Autodesk.Revit.DB.Wall.Create", "kind": "Method", "name": "Create"},
			},
			"total_matched": 4,
		}, nil
	})
	cs := connectDiscoveryClient(t, r, nil)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name:      "search_functions",
		Arguments: map[string]any{"query": "create a wall"},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	var out SearchFunctionsOut
	sc, _ := json.Marshal(res.StructuredContent)
	json.Unmarshal(sc, &out)
	if strings.Contains(out.Guidance, "Narrow") || strings.Contains(out.Guidance, "No members matched") {
		t.Errorf("out.Guidance = %q, want the middle reworded-retry wording for a small match set", out.Guidance)
	}
	if !strings.Contains(out.Guidance, "different wording") {
		t.Errorf("out.Guidance = %q, want the reworded-retry nudge", out.Guidance)
	}
}

// TestSearchFunctionsToolEmptyGuidance pins the stronger guidance wording for a
// zero-match search -- the moment an agent is likeliest to wrongly conclude the
// API is absent (POC: recall@1 ~53%, but the target is in the candidate pool
// ~93% of the time under different wording).
func TestSearchFunctionsToolEmptyGuidance(t *testing.T) {
	r := discovery.NewRouter(registry.New())
	attachFakeDiscoveryInstance(t, r, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		return map[string]any{"results": []any{}, "total_matched": 0}, nil
	})
	cs := connectDiscoveryClient(t, r, nil)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name:      "search_functions",
		Arguments: map[string]any{"query": "nonexistent gibberish"},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	if res.IsError {
		t.Fatalf("unexpected tool error: %+v", res.Content)
	}
	var out SearchFunctionsOut
	sc, _ := json.Marshal(res.StructuredContent)
	json.Unmarshal(sc, &out)
	if len(out.Results) != 0 {
		t.Fatalf("out.Results = %+v, want empty", out.Results)
	}
	if !strings.Contains(out.Guidance, "No members matched") {
		t.Errorf("out.Guidance = %q, want the empty-result retry wording", out.Guidance)
	}
	if !strings.Contains(out.Guidance, "list_functions") {
		t.Errorf("out.Guidance = %q, want it to point at list_functions as the fallback", out.Guidance)
	}
}

func TestDescribeFunctionToolSingleOverload(t *testing.T) {
	r := discovery.NewRouter(registry.New())
	attachFakeDiscoveryInstance(t, r, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		if method != "describe_function" {
			t.Errorf("method = %q, want describe_function", method)
		}
		return map[string]any{
			"member_id":      "M:Autodesk.Revit.DB.Document.Delete(Autodesk.Revit.DB.ElementId)",
			"kind":           "Method",
			"namespace":      "Autodesk.Revit.DB",
			"declaring_type": "Autodesk.Revit.DB.Document",
			"name":           "Delete",
			"signature":      "ICollection<ElementId> Delete(ElementId elementId)",
			"summary":        "Deletes an element.",
			"parameters": []any{
				map[string]any{"name": "elementId", "type": "ElementId", "description": "the element to delete"},
			},
			"returns":        "the ids of elements deleted",
			"overload_count": 3,
		}, nil
	})
	cs := connectDiscoveryClient(t, r, nil)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name:      "describe_function",
		Arguments: map[string]any{"member": "Autodesk.Revit.DB.Document.Delete"},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	if res.IsError {
		t.Fatalf("unexpected tool error: %+v", res.Content)
	}
	var out DescribeFunctionOut
	sc, _ := json.Marshal(res.StructuredContent)
	json.Unmarshal(sc, &out)
	if out.Result["name"] != "Delete" || out.Result["overload_count"] != float64(3) {
		t.Errorf("out.Result = %+v", out.Result)
	}
}

func TestDescribeFunctionToolMultipleOverloads(t *testing.T) {
	r := discovery.NewRouter(registry.New())
	attachFakeDiscoveryInstance(t, r, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		return map[string]any{
			"member": "Autodesk.Revit.DB.Document.Delete",
			"overloads": []any{
				map[string]any{"member_id": "M:...1", "signature": "ICollection<ElementId> Delete(ElementId elementId)"},
				map[string]any{"member_id": "M:...2", "signature": "ICollection<ElementId> Delete(ICollection<ElementId> elementIds)"},
			},
		}, nil
	})
	cs := connectDiscoveryClient(t, r, nil)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name:      "describe_function",
		Arguments: map[string]any{"member": "Autodesk.Revit.DB.Document.Delete"},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	if res.IsError {
		t.Fatalf("unexpected tool error: %+v", res.Content)
	}
	var out DescribeFunctionOut
	sc, _ := json.Marshal(res.StructuredContent)
	json.Unmarshal(sc, &out)
	overloads, ok := out.Result["overloads"].([]any)
	if !ok || len(overloads) != 2 {
		t.Errorf("out.Result[overloads] = %+v", out.Result["overloads"])
	}
}

func TestListFunctionsToolNoInstanceIsToolError(t *testing.T) {
	r := discovery.NewRouter(registry.New())
	cs := connectDiscoveryClient(t, r, nil)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name:      "list_functions",
		Arguments: map[string]any{},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	if !res.IsError {
		t.Fatalf("expected IsError=true when no instance is connected")
	}
	text, ok := res.Content[0].(*mcp.TextContent)
	if !ok {
		t.Fatalf("expected TextContent, got %T", res.Content[0])
	}
	var out ListFunctionsOut
	if err := json.Unmarshal([]byte(text.Text), &out); err != nil {
		t.Fatalf("decoding error content: %v", err)
	}
	if out.Error == nil || out.Error.Code != "no-instance-connected" {
		t.Errorf("out.Error = %+v, want no-instance-connected", out.Error)
	}
}

func TestDescribeFunctionToolUnknownInstanceIsToolError(t *testing.T) {
	r := discovery.NewRouter(registry.New())
	cs := connectDiscoveryClient(t, r, nil)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name:      "describe_function",
		Arguments: map[string]any{"instance_id": "ghost", "member": "Autodesk.Revit.DB.Document.Delete"},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	if !res.IsError {
		t.Fatalf("expected IsError=true for unknown instance_id")
	}
	text := res.Content[0].(*mcp.TextContent)
	var out DescribeFunctionOut
	json.Unmarshal([]byte(text.Text), &out)
	if out.Error == nil || out.Error.Code != "instance-not-found" {
		t.Errorf("out.Error = %+v, want instance-not-found", out.Error)
	}
}

func TestDiscoveryToolsMalformedWireResponseIsToolError(t *testing.T) {
	r := discovery.NewRouter(registry.New())
	attachFakeDiscoveryInstance(t, r, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		// Return something that doesn't decode into the expected shape:
		// a bare JSON string instead of an object.
		return "not an object", nil
	})
	cs := connectDiscoveryClient(t, r, nil)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name:      "list_functions",
		Arguments: map[string]any{},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	if !res.IsError {
		t.Fatalf("expected IsError=true for a malformed wire response")
	}
	text := res.Content[0].(*mcp.TextContent)
	var out ListFunctionsOut
	if err := json.Unmarshal([]byte(text.Text), &out); err != nil {
		t.Fatalf("decoding error content: %v", err)
	}
	if out.Error == nil || out.Error.Code != "wire-response-malformed" {
		t.Errorf("out.Error = %+v, want wire-response-malformed", out.Error)
	}
}

func TestDescribeFunctionToolNeitherMemberNorMemberIDIsToolError(t *testing.T) {
	r := discovery.NewRouter(registry.New())
	cs := connectDiscoveryClient(t, r, nil)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name:      "describe_function",
		Arguments: map[string]any{},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	if !res.IsError {
		t.Fatalf("expected IsError=true when neither member nor member_id is given")
	}
	text, ok := res.Content[0].(*mcp.TextContent)
	if !ok {
		t.Fatalf("expected TextContent, got %T", res.Content[0])
	}
	var out DescribeFunctionOut
	if err := json.Unmarshal([]byte(text.Text), &out); err != nil {
		t.Fatalf("decoding error content: %v", err)
	}
	if out.Error == nil || out.Error.Code != "missing-required-param" {
		t.Errorf("out.Error = %+v, want missing-required-param", out.Error)
	}
}

func TestDescribeFunctionToolMemberIDOnlyIsAcceptedAndForwarded(t *testing.T) {
	r := discovery.NewRouter(registry.New())
	var gotParams map[string]any
	attachFakeDiscoveryInstance(t, r, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		if method != "describe_function" {
			t.Errorf("method = %q, want describe_function", method)
		}
		if err := json.Unmarshal(params, &gotParams); err != nil {
			t.Fatalf("unmarshalling forwarded params: %v", err)
		}
		return map[string]any{
			"member_id":      "M:Autodesk.Revit.DB.Document.Delete(Autodesk.Revit.DB.ElementId)",
			"kind":           "Method",
			"name":           "Delete",
			"signature":      "ICollection<ElementId> Delete(ElementId elementId)",
			"overload_count": 3,
		}, nil
	})
	cs := connectDiscoveryClient(t, r, nil)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name:      "describe_function",
		Arguments: map[string]any{"member_id": "M:Autodesk.Revit.DB.Document.Delete(Autodesk.Revit.DB.ElementId)"},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	if res.IsError {
		t.Fatalf("unexpected tool error: %+v", res.Content)
	}
	if _, hasMember := gotParams["member"]; hasMember {
		t.Errorf("forwarded params should omit member when empty, got %+v", gotParams)
	}
	if gotParams["member_id"] != "M:Autodesk.Revit.DB.Document.Delete(Autodesk.Revit.DB.ElementId)" {
		t.Errorf("forwarded params[member_id] = %v, want the given member_id", gotParams["member_id"])
	}
	var out DescribeFunctionOut
	sc, _ := json.Marshal(res.StructuredContent)
	json.Unmarshal(sc, &out)
	if out.Result["name"] != "Delete" {
		t.Errorf("out.Result = %+v", out.Result)
	}
}

func TestDescribeFunctionInputSchemaMemberOptionalAndNoOverloadIndex(t *testing.T) {
	r := discovery.NewRouter(registry.New())
	cs := connectDiscoveryClient(t, r, nil)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	list, err := cs.ListTools(ctx, nil)
	if err != nil {
		t.Fatalf("ListTools: %v", err)
	}
	var schema map[string]any
	for _, tool := range list.Tools {
		if tool.Name == "describe_function" {
			b, _ := json.Marshal(tool.InputSchema)
			if err := json.Unmarshal(b, &schema); err != nil {
				t.Fatalf("decoding describe_function InputSchema: %v", err)
			}
		}
	}
	if schema == nil {
		t.Fatalf("describe_function tool not found")
	}

	if required, ok := schema["required"].([]any); ok {
		for _, r := range required {
			if r == "member" {
				t.Errorf("member should no longer be required, schema required = %+v", required)
			}
		}
	}

	props, ok := schema["properties"].(map[string]any)
	if !ok {
		t.Fatalf("schema properties = %+v, want a map", schema["properties"])
	}
	if _, ok := props["overload_index"]; ok {
		t.Errorf("overload_index should be removed from the schema, got properties = %+v", props)
	}
	if _, ok := props["member_id"]; !ok {
		t.Errorf("member_id should still be present in the schema, got properties = %+v", props)
	}
	if _, ok := props["member"]; !ok {
		t.Errorf("member should still be present in the schema (just optional), got properties = %+v", props)
	}
}

func TestDiscoveryToolsAreRegisteredWithExpectedNames(t *testing.T) {
	r := discovery.NewRouter(registry.New())
	cs := connectDiscoveryClient(t, r, nil)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	list, err := cs.ListTools(ctx, nil)
	if err != nil {
		t.Fatalf("ListTools: %v", err)
	}
	want := map[string]bool{"list_functions": false, "search_functions": false, "describe_function": false}
	for _, tool := range list.Tools {
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
