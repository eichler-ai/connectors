package mcpserver

import (
	"context"
	"encoding/json"
	"net"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/execution"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/transport"
)

// connectClient wires an in-process MCP client to a server that has the
// tools registered, using the SDK's in-memory transport pair.
func connectClient(t *testing.T, mgr *execution.Manager) *mcp.ClientSession {
	t.Helper()
	server := mcp.NewServer(&mcp.Implementation{Name: "revit-mcp-server-test", Version: "0.0.0"}, nil)
	Register(server, mgr)

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

// attachFakeInstance wires mgr's "inst-1" to a fake add-in peer driven by
// handler, in the same style as internal/execution's own tests, so these
// tests exercise the tool -> manager -> wire path end to end.
func attachFakeInstance(t *testing.T, mgr *execution.Manager, instanceID string, handler transport.RequestHandler) {
	t.Helper()
	brokerSide, addinSide := net.Pipe()
	brokerConn := transport.NewConn(brokerSide)
	addinConn := transport.NewConn(addinSide)
	addinConn.SetRequestHandler(handler)
	go brokerConn.Serve()
	go addinConn.Serve()
	t.Cleanup(func() { brokerConn.Close(); addinConn.Close() })
	mgr.AttachInstance(instanceID, brokerConn)
}

func TestExecuteScriptToolSuccess(t *testing.T) {
	mgr := execution.NewManager()
	attachFakeInstance(t, mgr, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return map[string]any{"status": "success", "execution_id": p["execution_id"], "output": "hello"}, nil
	})
	cs := connectClient(t, mgr)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name: "execute_script",
		Arguments: map[string]any{
			"instance_id": "inst-1",
			"document_id": "doc-1",
			"script":      "return \"hello\";",
		},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	if res.IsError {
		t.Fatalf("unexpected tool error: %+v", res.Content)
	}

	var out ExecutionOut
	sc, _ := json.Marshal(res.StructuredContent)
	if err := json.Unmarshal(sc, &out); err != nil {
		t.Fatalf("decoding structured content: %v", err)
	}
	if out.Status != "success" || out.Output != "hello" {
		t.Errorf("out = %+v", out)
	}
}

// TestExecuteScriptToolAddInReportedErrorIsToolError is a regression test:
// a script that fails on the add-in side (a real wire round trip, reported
// back as a normal Result with status:"error") must surface as IsError:true
// exactly like a wire-level failure — not as a "successful" tool call whose
// structured content just happens to say status:"error", which the calling
// agent has no MCP-level signal to notice.
func TestExecuteScriptToolAddInReportedErrorIsToolError(t *testing.T) {
	mgr := execution.NewManager()
	attachFakeInstance(t, mgr, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return map[string]any{
			"status":       "error",
			"execution_id": p["execution_id"],
			"error": map[string]any{
				"severity": "error",
				"code":     "script_exception",
				"source":   "mcp-bridge.core.execution",
				"message":  "System.NullReferenceException: Object reference not set",
			},
		}, nil
	})
	cs := connectClient(t, mgr)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name: "execute_script",
		Arguments: map[string]any{
			"instance_id": "inst-1",
			"document_id": "doc-1",
			"script":      "throw new Exception();",
		},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	if !res.IsError {
		t.Fatalf("expected IsError=true for an add-in-reported script error, got a normal result")
	}
}

func TestExecuteScriptToolUnknownInstanceIsToolError(t *testing.T) {
	mgr := execution.NewManager()
	cs := connectClient(t, mgr)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name: "execute_script",
		Arguments: map[string]any{
			"instance_id": "ghost",
			"document_id": "doc-1",
			"script":      "1+1",
		},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	if !res.IsError {
		t.Fatalf("expected IsError=true for an unknown instance")
	}
	if len(res.Content) == 0 {
		t.Fatal("expected error content describing the failure")
	}
	text, ok := res.Content[0].(*mcp.TextContent)
	if !ok {
		t.Fatalf("expected TextContent, got %T", res.Content[0])
	}
	var out ExecutionOut
	if err := json.Unmarshal([]byte(text.Text), &out); err != nil {
		t.Fatalf("decoding error content: %v", err)
	}
	if out.Error == nil {
		t.Fatal("expected Error diagnostic record in output")
	}
	if out.Error.Code != "instance_not_found" {
		t.Errorf("Error.Code = %q, want instance_not_found", out.Error.Code)
	}
	if out.Error.Severity != "error" {
		t.Errorf("Error.Severity = %q, want error", out.Error.Severity)
	}
	if out.Error.Source == "" {
		t.Errorf("Error.Source should be set")
	}
}

func TestPollAndCancelToolsRouteToManager(t *testing.T) {
	mgr := execution.NewManager()
	attachFakeInstance(t, mgr, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		id := p["execution_id"].(string)
		switch method {
		case "execute_script":
			return map[string]any{"status": "running", "execution_id": id}, nil
		case "poll_execution":
			return map[string]any{"status": "success", "execution_id": id, "output": "final"}, nil
		case "cancel_execution":
			return map[string]any{"status": "cancelled", "execution_id": id}, nil
		}
		return nil, &transport.RPCError{Code: -1, Message: "unexpected method"}
	})
	cs := connectClient(t, mgr)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	startRes, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name:      "execute_script",
		Arguments: map[string]any{"instance_id": "inst-1", "document_id": "doc-1", "script": "slow", "timeout_ms": 50},
	})
	if err != nil || startRes.IsError {
		t.Fatalf("execute_script: err=%v res=%+v", err, startRes)
	}
	var startOut ExecutionOut
	sc, _ := json.Marshal(startRes.StructuredContent)
	json.Unmarshal(sc, &startOut)
	if startOut.Status != "running" {
		t.Fatalf("Status = %q, want running", startOut.Status)
	}

	pollRes, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name:      "poll_execution",
		Arguments: map[string]any{"execution_id": startOut.ExecutionID},
	})
	if err != nil || pollRes.IsError {
		t.Fatalf("poll_execution: err=%v res=%+v", err, pollRes)
	}
	var pollOut ExecutionOut
	sc, _ = json.Marshal(pollRes.StructuredContent)
	json.Unmarshal(sc, &pollOut)
	if pollOut.Status != "success" || pollOut.Output != "final" {
		t.Errorf("pollOut = %+v", pollOut)
	}

	// cancel_execution against the now-terminal execution should just
	// return its cached terminal result (per manager semantics), not error.
	cancelRes, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name:      "cancel_execution",
		Arguments: map[string]any{"execution_id": startOut.ExecutionID},
	})
	if err != nil || cancelRes.IsError {
		t.Fatalf("cancel_execution: err=%v res=%+v", err, cancelRes)
	}
}

func TestToolsAreRegisteredWithExpectedNames(t *testing.T) {
	mgr := execution.NewManager()
	cs := connectClient(t, mgr)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	list, err := cs.ListTools(ctx, nil)
	if err != nil {
		t.Fatalf("ListTools: %v", err)
	}
	want := map[string]bool{"execute_script": false, "poll_execution": false, "cancel_execution": false}
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
