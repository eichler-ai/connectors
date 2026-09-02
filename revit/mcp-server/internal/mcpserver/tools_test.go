package mcpserver

import (
	"context"
	"encoding/json"
	"net"
	"strings"
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

// TestExecuteScriptToolKeepsReturnValueSeparateFromOutput is issue #117's
// broker half. The add-in used to fold a script's returned value into
// output behind a blank line, so Revit's own console writes during a run
// ("PlayerServer:Warning:No subscriber registered.") arrived ahead of the
// answer in the same field with nothing marking the boundary. Both ends
// changed; this pins that the broker actually carries the new field
// through to the agent rather than dropping it on the floor, which is
// exactly what an unchanged Result struct would have done silently.
func TestExecuteScriptToolKeepsReturnValueSeparateFromOutput(t *testing.T) {
	mgr := execution.NewManager()
	attachFakeInstance(t, mgr, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return map[string]any{
			"status":       "success",
			"execution_id": p["execution_id"],
			"output":       "PlayerServer:Warning:No subscriber registered.\n",
			"return_value": `C:\dev\fixtures\ProjectFresh.rvt`,
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
			"script":      "return doc.PathName;",
		},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}

	var out ExecutionOut
	sc, _ := json.Marshal(res.StructuredContent)
	if err := json.Unmarshal(sc, &out); err != nil {
		t.Fatalf("decoding structured content: %v", err)
	}
	if out.ReturnValue != `C:\dev\fixtures\ProjectFresh.rvt` {
		t.Errorf("return_value = %q, want the script's returned value carried through untouched", out.ReturnValue)
	}
	if !strings.Contains(out.Output, "PlayerServer") {
		t.Errorf("output = %q, want the captured stdout still present in its own field", out.Output)
	}
	if strings.Contains(out.ReturnValue, "PlayerServer") {
		t.Errorf("return_value = %q, want no captured stdout mixed into it", out.ReturnValue)
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

// TestUndoRedoToolsForwardToTheAddIn pins the #146 Phase 2c tools: each
// forwards its direction, confirm and timeout on the `undo_redo` wire method,
// and the add-in's terminal answer (status, mutations, notices) comes back
// as the tool result. Busy when a script is in flight is the manager's
// existing rule and is pinned separately below.
func TestUndoRedoToolsForwardToTheAddIn(t *testing.T) {
	mgr := execution.NewManager()
	var seen []map[string]any
	attachFakeInstance(t, mgr, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		if method != "undo_redo" {
			return nil, &transport.RPCError{Code: -32601, Message: "unexpected method " + method}
		}
		var p map[string]any
		json.Unmarshal(params, &p)
		seen = append(seen, p)
		return map[string]any{
			"status":    "success",
			"mutations": map[string]any{"net_created": 0, "net_modified": 0, "net_deleted": 1, "by_category": map[string]any{}, "truncated": false},
			"notices":   []map[string]any{{"severity": "info", "code": "undo-reverted-connector-work", "source": "mcp-bridge.core.execution", "message": "undo reverted 'MCP: 1 Levels created'."}},
		}, nil
	})
	cs := connectClient(t, mgr)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	res, err := cs.CallTool(ctx, &mcp.CallToolParams{Name: "undo", Arguments: map[string]any{"instance_id": "inst-1", "confirm": true, "timeout_ms": 99_000, "document_id": "doc-1"}})
	if err != nil {
		t.Fatalf("CallTool undo: %v", err)
	}
	if res.IsError {
		t.Fatalf("unexpected tool error: %+v", res.Content)
	}
	var out ExecutionOut
	sc, _ := json.Marshal(res.StructuredContent)
	if err := json.Unmarshal(sc, &out); err != nil {
		t.Fatalf("decoding structured content: %v", err)
	}
	if out.Mutations == nil || out.Mutations.Deleted != 1 || len(out.Notices) != 1 || out.Notices[0].Code != "undo-reverted-connector-work" {
		t.Errorf("undo result did not carry the add-in's answer: %+v", out)
	}
	if got := seen[0]; got["direction"] != "undo" || got["confirm"] != true || got["timeout_ms"].(float64) != 30_000 || got["document_id"] != "doc-1" {
		t.Errorf("wire params = %v (want direction undo, confirm true, timeout clamped to 30000, document_id doc-1)", got)
	}
	if id, _ := seen[0]["execution_id"].(string); id == "" {
		t.Errorf("an undo is an execution: the broker must mint and send an execution_id, got %v", seen[0]["execution_id"])
	}
	if out.ExecutionID == "" {
		t.Errorf("the tool result must carry the undo's execution_id (it is pollable), got %+v", out)
	}
	// Settled: the instance is free again for the next call.
	if st := mgr.StatusForInstance("inst-1"); st != execution.StatusIdle {
		t.Errorf("instance should be idle after a terminal undo answer, got %q", st)
	}

	if _, err := cs.CallTool(ctx, &mcp.CallToolParams{Name: "redo", Arguments: map[string]any{"instance_id": "inst-1"}}); err != nil {
		t.Fatalf("CallTool redo: %v", err)
	}
	if got := seen[1]; got["direction"] != "redo" || got["confirm"] != false || got["timeout_ms"].(float64) != 10_000 {
		t.Errorf("redo wire params = %v (want direction redo, confirm false, default timeout 10000)", got)
	}
	if _, present := seen[1]["document_id"]; present {
		t.Errorf("an omitted document_id must not be sent, got %v", seen[1]["document_id"])
	}
}

// TestUndoIsBusyWhileAScriptIsInFlight pins the broker half of the gate: the
// add-in never sees an undo_redo while an execution it owns is non-terminal.
func TestUndoIsBusyWhileAScriptIsInFlight(t *testing.T) {
	mgr := execution.NewManager()
	var methods []string
	attachFakeInstance(t, mgr, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		methods = append(methods, method)
		// The script never finishes: answer pending so the instance stays busy.
		return map[string]any{"status": "pending", "execution_id": p["execution_id"]}, nil
	})
	cs := connectClient(t, mgr)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	if _, err := cs.CallTool(ctx, &mcp.CallToolParams{Name: "execute_script", Arguments: map[string]any{"instance_id": "inst-1", "document_id": "doc-1", "script": "1", "timeout_ms": 100}}); err != nil {
		t.Fatalf("execute_script: %v", err)
	}
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{Name: "undo", Arguments: map[string]any{"instance_id": "inst-1", "confirm": true}})
	if err != nil {
		t.Fatalf("undo: %v", err)
	}
	var out ExecutionOut
	sc, _ := json.Marshal(res.StructuredContent)
	json.Unmarshal(sc, &out)
	if out.Status != "busy" || out.ExecutionID == "" {
		t.Errorf("undo during a pending script must answer busy pointing at it, got %+v", out)
	}
	for _, m := range methods {
		if m == "undo_redo" {
			t.Errorf("undo_redo reached the add-in while a script was in flight")
		}
	}
}

// TestExecuteScriptToolLabelReachesTheWire pins the #146 Phase 2b `label`
// argument: sent to the add-in verbatim when given, and absent from the
// params -- not sent as "" -- when not, so an older add-in sees exactly the
// request shape it always did.
func TestExecuteScriptToolLabelReachesTheWire(t *testing.T) {
	mgr := execution.NewManager()
	var seen []map[string]any
	attachFakeInstance(t, mgr, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		seen = append(seen, p)
		return map[string]any{"status": "success", "execution_id": p["execution_id"]}, nil
	})
	cs := connectClient(t, mgr)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	for _, args := range []map[string]any{
		{"instance_id": "inst-1", "document_id": "doc-1", "script": "1", "label": "create L1 walls"},
		{"instance_id": "inst-1", "document_id": "doc-1", "script": "1"},
	} {
		if _, err := cs.CallTool(ctx, &mcp.CallToolParams{Name: "execute_script", Arguments: args}); err != nil {
			t.Fatalf("CallTool: %v", err)
		}
	}
	if got, _ := seen[0]["label"].(string); got != "create L1 walls" {
		t.Errorf("label did not reach the wire verbatim: %v", seen[0]["label"])
	}
	if _, present := seen[1]["label"]; present {
		t.Errorf("an omitted label must not be sent at all, got %v", seen[1]["label"])
	}
}

// TestExecuteScriptToolMutationsRoundTrip pins the #146 Phase 2 `mutations`
// field's passage through execution.Result and ExecutionOut: the add-in
// computes it, the broker must carry it verbatim, and it must be absent (not
// zeroed) when the add-in sent none -- a read-only run has no field at all.
func TestExecuteScriptToolMutationsRoundTrip(t *testing.T) {
	mgr := execution.NewManager()
	calls := 0
	attachFakeInstance(t, mgr, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		calls++
		res := map[string]any{"status": "success", "execution_id": p["execution_id"]}
		if calls == 1 {
			res["mutations"] = map[string]any{
				"net_created": 2, "net_modified": 1, "net_deleted": 0,
				"by_category": map[string]any{"Walls": map[string]any{"net_created": 2, "net_modified": 0}},
				"truncated":   false,
			}
		}
		return res, nil
	})
	cs := connectClient(t, mgr)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	call := func(script string) ExecutionOut {
		res, err := cs.CallTool(ctx, &mcp.CallToolParams{
			Name:      "execute_script",
			Arguments: map[string]any{"instance_id": "inst-1", "document_id": "doc-1", "script": script},
		})
		if err != nil {
			t.Fatalf("CallTool: %v", err)
		}
		var out ExecutionOut
		sc, _ := json.Marshal(res.StructuredContent)
		if err := json.Unmarshal(sc, &out); err != nil {
			t.Fatalf("decoding structured content: %v", err)
		}
		return out
	}

	withWrites := call("Level.Create(Document, 1);")
	if withWrites.Mutations == nil || withWrites.Mutations.Created != 2 || withWrites.Mutations.Modified != 1 ||
		withWrites.Mutations.ByCategory["Walls"].Created != 2 {
		t.Errorf("Mutations = %+v", withWrites.Mutations)
	}
	readOnly := call("return 1;")
	if readOnly.Mutations != nil {
		t.Errorf("a result the add-in sent without mutations must carry none, got %+v", readOnly.Mutations)
	}
}

// TestExecuteScriptToolOverwriteOutputFilesAndFilesRoundTrip is a regression
// test for PRD §09's file-exchange wire fields: overwrite_output_files set
// on the tool call must reach the add-in's execute_script params, and a
// files[] array in the add-in's reply must round-trip into
// ExecutionOut.Files via toolResult/the full tool handler path — the same
// shape as document_id/notices already round-trip.
func TestExecuteScriptToolOverwriteOutputFilesAndFilesRoundTrip(t *testing.T) {
	mgr := execution.NewManager()
	var gotOverwrite bool
	attachFakeInstance(t, mgr, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		gotOverwrite, _ = p["overwrite_output_files"].(bool)
		return map[string]any{
			"status":       "success",
			"execution_id": p["execution_id"],
			"files": []map[string]any{
				{"name": "view.png", "path": "exports/view.png", "status": "published"},
			},
		}, nil
	})
	cs := connectClient(t, mgr)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name: "execute_script",
		Arguments: map[string]any{
			"instance_id":            "inst-1",
			"document_id":            "doc-1",
			"script":                 "Publish(\"a.png\");",
			"overwrite_output_files": true,
		},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	if res.IsError {
		t.Fatalf("unexpected tool error: %+v", res.Content)
	}
	if !gotOverwrite {
		t.Error("overwrite_output_files did not reach the wire params as true")
	}

	var out ExecutionOut
	sc, _ := json.Marshal(res.StructuredContent)
	if err := json.Unmarshal(sc, &out); err != nil {
		t.Fatalf("decoding structured content: %v", err)
	}
	if len(out.Files) != 1 || out.Files[0].Name != "view.png" || out.Files[0].Path != "exports/view.png" || out.Files[0].Status != "published" {
		t.Errorf("Files = %+v", out.Files)
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
	if out.Error.Code != "instance-not-found" {
		t.Errorf("Error.Code = %q, want instance-not-found", out.Error.Code)
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

// TestExecuteScriptToolForwardsConfirmLifecycleActions is the tool-surface half
// of PRD §14's confirmation gate: the argument must exist on execute_script's
// input schema under the name the skill file tells agents to use, and must
// reach the add-in's params. Driven through the real tool handler (not
// execution.Manager directly) because the schema binding is exactly what could
// silently drop it — an argument the SDK does not know about is ignored, not
// rejected, so a typo'd json tag would fail nothing else in this suite.
func TestExecuteScriptToolForwardsConfirmLifecycleActions(t *testing.T) {
	mgr := execution.NewManager()
	var gotConfirm bool
	attachFakeInstance(t, mgr, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		gotConfirm, _ = p["confirm_lifecycle_actions"].(bool)
		return map[string]any{"status": "success", "execution_id": p["execution_id"]}, nil
	})
	cs := connectClient(t, mgr)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{
		Name: "execute_script",
		Arguments: map[string]any{
			"instance_id":               "inst-1",
			"document_id":               "doc-1",
			"script":                    "Document.Save();",
			"confirm_lifecycle_actions": true,
		},
	})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	if res.IsError {
		t.Fatalf("unexpected tool error: %+v", res.Content)
	}
	if !gotConfirm {
		t.Error("confirm_lifecycle_actions did not reach the wire params as true")
	}
}

// Issue #84. execute_script's description is the only place an agent is guaranteed to see before
// writing its first script, so the globals are named there. The list is maintained by hand in Go and
// reflected in C# (ScriptGlobals.GlobalNames); the C# side carries the tripwire that fails when the two
// drift (ScriptGlobalsDiscoverabilityTests). This test guards the Go half: that the names are present at
// all, and that the description routes an agent onward rather than trying to explain them here.
//
// Issue #91 cut this to five names. The seven connector-provided functions are deliberately NOT asserted
// on -- they are not in the description any more, and putting them back would recreate exactly the
// hand-maintained Go copy that drifted three ways before #91. Their names now reach an agent through
// describe_function, from XML doc comments beside their own code.
func TestExecuteScriptDescription_NamesTheScriptGlobalsAndRoutesOnward(t *testing.T) {
	desc := executeScriptDescription

	// Asserted on the DELIMITED prose, not on the bare names, because two of the bare-name checks could
	// not fail (review finding, and the "assertion cannot fail" row in caveats.md): "Connector" is a
	// substring of both "Eichler.Connectors.Revit" and "Connector.Publish", and "Document" is a substring
	// of "UIDocument" -- all of which are separately asserted below. Deleting either global from the
	// description left the old loop green.
	for _, phrase := range []string{
		"Document, UIApplication, UIDocument and CancellationToken",
		"plus Connector,",
	} {
		if !strings.Contains(desc, phrase) {
			t.Errorf("execute_script description no longer contains %q; an agent has no way to discover "+
				"the globals it names (issue #84)", phrase)
		}
	}

	// The connector's own functions must be findable, and the description's job is to say WHERE rather
	// than to list them. Without the namespace an agent has a name it cannot look up.
	if !strings.Contains(desc, "Eichler.Connectors.Revit") {
		t.Error("execute_script description must name the connector's namespace, so an agent can find " +
			"its functions through search_functions/describe_function (issue #91)")
	}
	if !strings.Contains(desc, "Connector.Publish") {
		t.Error("execute_script description must show the calling form; a qualified path an agent " +
			"cannot compile is worse than no path at all (issue #91 D2)")
	}
	if !strings.Contains(desc, "get_skills") {
		t.Error("execute_script description must point at get_skills, which carries the transaction, " +
			"document-creation and file-exchange rules")
	}

	// Guards the specific staleness #91 introduced the risk of: the pre-#91 text told an agent that
	// search_functions "will never return these", which is now false -- the connector's API is indexed
	// as an add-in API. An agent that believes the old claim will not look.
	if strings.Contains(desc, "never return these") {
		t.Error("execute_script description still claims search_functions will never return the " +
			"connector's globals; since issue #91 they are indexed as an add-in API")
	}

	// Connector members are documented by describe_function now, not here. If one of these reappears,
	// the Go copy is back and so is the drift.
	// Every connector member that must NOT reappear. `Publish` is deliberately absent from this
	// list: it legitimately appears as part of the `Connector.Publish(path)` calling form asserted above.
	for _, name := range []string{
		"CreateProjectDocument", "CreateFamilyDocument", "WithTransaction", "Settle",
		"ExportsDirectory", "ImportsDirectory", "DialogResultOverrides",
	} {
		if strings.Contains(desc, name) {
			t.Errorf("execute_script description enumerates the connector member %q again; since "+
				"issue #91 it names only the Connector entry point, so this list cannot drift", name)
		}
	}
}
