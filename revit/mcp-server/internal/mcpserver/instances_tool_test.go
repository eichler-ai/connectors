package mcpserver

import (
	"context"
	"encoding/json"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/execution"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/registry"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/transport"
)

// connectInstancesClient wires an in-process MCP client to a server that has
// list_instances registered, using the SDK's in-memory transport pair —
// mirrors connectDiscoveryClient/connectClient's own established pattern.
func connectInstancesClient(t *testing.T, reg *registry.Registry, mgr *execution.Manager) *mcp.ClientSession {
	t.Helper()
	server := mcp.NewServer(&mcp.Implementation{Name: "revit-mcp-server-test", Version: "0.0.0"}, nil)
	RegisterInstances(server, reg, mgr)

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

// attachFakeInstance (wiring a fake add-in connection into mgr so
// ExecuteScript has somewhere to forward to) already exists in
// tools_test.go, same package -- reused here rather than redeclared.

func callListInstances(t *testing.T, cs *mcp.ClientSession) ListInstancesOut {
	t.Helper()
	res, err := cs.CallTool(context.Background(), &mcp.CallToolParams{Name: "list_instances", Arguments: map[string]any{}})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	raw, err := json.Marshal(res.StructuredContent)
	if err != nil {
		t.Fatalf("marshal StructuredContent: %v", err)
	}
	var out ListInstancesOut
	if err := json.Unmarshal(raw, &out); err != nil {
		t.Fatalf("unmarshal into ListInstancesOut: %v", err)
	}
	return out
}

func TestListInstancesToolEmptyRegistry(t *testing.T) {
	reg := registry.New()
	mgr := execution.NewManager()
	cs := connectInstancesClient(t, reg, mgr)

	out := callListInstances(t, cs)
	if len(out.Instances) != 0 {
		t.Errorf("expected no instances, got %+v", out.Instances)
	}
}

func TestListInstancesToolReturnsRegisteredInstanceIdleByDefault(t *testing.T) {
	reg := registry.New()
	mgr := execution.NewManager()
	reg.Register(&registry.Instance{
		InstanceID:   "inst-1",
		PID:          4242,
		RevitVersion: "2027",
		Documents: []registry.Document{
			{ID: "doc-abc", Title: "Sample.rvt", Path: `C:\Sample.rvt`, Workshared: true, Active: true},
		},
	})
	cs := connectInstancesClient(t, reg, mgr)

	out := callListInstances(t, cs)
	if len(out.Instances) != 1 {
		t.Fatalf("expected 1 instance, got %+v", out.Instances)
	}
	got := out.Instances[0]
	if got.InstanceID != "inst-1" || got.PID != 4242 || got.RevitVersion != "2027" {
		t.Errorf("instance fields wrong: %+v", got)
	}
	if got.Status != string(execution.StatusIdle) {
		t.Errorf("Status = %q, want idle for an instance with no execution/heartbeat activity", got.Status)
	}
	if len(got.Documents) != 1 {
		t.Fatalf("expected 1 document, got %+v", got.Documents)
	}
	doc := got.Documents[0]
	if doc.DocumentID != "doc-abc" || doc.Title != "Sample.rvt" || !doc.Workshared || !doc.Active {
		t.Errorf("document fields wrong (workshared must round-trip): %+v", doc)
	}
}

func TestListInstancesToolReflectsBusyExecutionStatus(t *testing.T) {
	reg := registry.New()
	mgr := execution.NewManager()
	reg.Register(&registry.Instance{InstanceID: "inst-1"})
	attachFakeInstance(t, mgr, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return execution.Result{Status: execution.StatusRunning, ExecutionID: p["execution_id"].(string)}, nil
	})
	if _, drec := mgr.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 100, 60000, false); drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	cs := connectInstancesClient(t, reg, mgr)
	out := callListInstances(t, cs)
	if len(out.Instances) != 1 || out.Instances[0].Status != string(execution.StatusBusy) {
		t.Errorf("expected exactly 1 instance with status busy, got %+v", out.Instances)
	}
}

func TestListInstancesToolUnresponsiveOverridesIdle(t *testing.T) {
	reg := registry.New()
	mgr := execution.NewManager()
	reg.Register(&registry.Instance{InstanceID: "inst-1"})
	// Force unresponsive without a real elapsed-time wait: register at a
	// ConnectedSince far enough in the past that IsResponsive's fallback
	// (no ping ever recorded) already exceeds the threshold as of now.
	past := time.Now().Add(-registry.UnresponsiveThreshold - time.Second)
	reg.Register(&registry.Instance{InstanceID: "inst-1", ConnectedSince: past})

	cs := connectInstancesClient(t, reg, mgr)
	out := callListInstances(t, cs)
	if len(out.Instances) != 1 || out.Instances[0].Status != "unresponsive" {
		t.Errorf("expected exactly 1 instance with status unresponsive, got %+v", out.Instances)
	}
}

func TestListInstancesToolUnrecoverableBeatsUnresponsive(t *testing.T) {
	reg := registry.New()
	mgr := execution.NewManager()
	// An add-in reporting StatusUnrecoverable directly (the wire result
	// "exactly like" the broker's own grace-period escalation, per
	// execution.go's CancelExecution doc comment) latches m.unrecoverable
	// via settle() -- the simplest way to reach this state in a test
	// without driving the full grace-period timer machinery.
	attachFakeInstance(t, mgr, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		var p map[string]any
		json.Unmarshal(params, &p)
		return execution.Result{Status: execution.StatusUnrecoverable, ExecutionID: p["execution_id"].(string)}, nil
	})
	past := time.Now().Add(-registry.UnresponsiveThreshold - time.Second)
	reg.Register(&registry.Instance{InstanceID: "inst-1", ConnectedSince: past})

	if _, drec := mgr.ExecuteScript(context.Background(), "inst-1", "doc-1", "slow", 50, 60000, false); drec != nil {
		t.Fatalf("ExecuteScript: %+v", drec)
	}

	cs := connectInstancesClient(t, reg, mgr)
	out := callListInstances(t, cs)
	if len(out.Instances) != 1 || out.Instances[0].Status != string(execution.StatusUnrecoverable) {
		t.Errorf("expected exactly 1 instance with status unrecoverable (beating an also-true unresponsive condition), got %+v", out.Instances)
	}
}
