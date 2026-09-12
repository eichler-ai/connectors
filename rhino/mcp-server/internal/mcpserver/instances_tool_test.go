package mcpserver

import (
	"context"
	"encoding/json"
	"strings"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/execution"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/registry"
)

func callListInstances(t *testing.T, reg *registry.Registry, now time.Time) ListInstancesOut {
	t.Helper()
	s := mcp.NewServer(&mcp.Implementation{Name: "test", Version: "0"}, nil)
	RegisterInstances(s, reg, func() time.Time { return now })
	ct, st := mcp.NewInMemoryTransports()
	ctx := context.Background()
	ss, err := s.Connect(ctx, st, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer ss.Close()
	client := mcp.NewClient(&mcp.Implementation{Name: "c", Version: "0"}, nil)
	cs, err := client.Connect(ctx, ct, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer cs.Close()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{Name: "list_instances", Arguments: map[string]any{}})
	if err != nil {
		t.Fatal(err)
	}
	if res.IsError {
		t.Fatalf("unexpected error result: %+v", res)
	}
	raw, _ := json.Marshal(res.StructuredContent)
	var out ListInstancesOut
	if err := json.Unmarshal(raw, &out); err != nil {
		t.Fatal(err)
	}
	return out
}

func TestEmptyRegistryGivesGuidanceAndAnEmptyArray(t *testing.T) {
	out := callListInstances(t, registry.New(), time.Now())
	if out.Instances == nil || len(out.Instances) != 0 {
		t.Fatalf("instances = %v", out.Instances)
	}
	if !strings.Contains(out.Guidance, "MCPBridgeStatus") {
		t.Fatalf("guidance = %q", out.Guidance)
	}
}

func TestInstancesCarryEveryFieldAndStatus(t *testing.T) {
	reg := registry.New()
	now := time.Now()
	e := reg.Register(&registry.Instance{InstanceID: "i1", PID: 7, RhinoVersion: "8.35", Platform: "macos", BridgeVersion: "dev",
		Documents: []registry.Document{{ID: "doc-x", Title: "Tower", Path: "/t.3dm", Active: true,
			LastRun: &execution.LastRun{ExecutionID: "exec-1", AgentClientID: "srvB", FinishedAt: "t", Status: "success", Label: "box", ChangedDocument: true}}}}, 0, now)
	reg.RecordPing("i1", e, now, &registry.MemorySample{WorkingSetMB: 9})
	// i2's plug-in reports busy (an older bridge that reports nothing reads idle, as i1).
	reg.Register(&registry.Instance{InstanceID: "i2", PID: 8, RhinoVersion: "8.35", Platform: "windows", ExecutionState: "busy"}, 0, now.Add(time.Second))

	out := callListInstances(t, reg, now.Add(time.Second))
	if len(out.Instances) != 2 {
		t.Fatalf("got %d", len(out.Instances))
	}
	a := out.Instances[0]
	if a.InstanceID != "i1" || a.PID != 7 || a.Platform != "macos" || a.BridgeVersion != "dev" || a.Status != "idle" || a.Memory == nil || a.Memory.WorkingSetMB != 9 {
		t.Fatalf("i1 = %+v", a)
	}
	if len(a.Documents) != 1 || a.Documents[0].DocumentID != "doc-x" || !a.Documents[0].Active || a.Documents[0].Path != "/t.3dm" {
		t.Fatalf("docs = %+v", a.Documents)
	}
	if lr := a.Documents[0].LastRun; lr == nil || lr.ExecutionID != "exec-1" || lr.AgentClientID != "srvB" || lr.Label != "box" {
		t.Fatalf("last_run = %+v (PRD §05)", a.Documents[0].LastRun)
	}
	if out.Instances[1].Status != "busy" || out.Instances[1].Documents == nil {
		t.Fatalf("i2 = %+v", out.Instances[1])
	}
	if out.Guidance != "" {
		t.Fatal("no guidance when instances exist")
	}
}

func TestGrasshopperDocumentsSurfaceInstanceLevel(t *testing.T) {
	reg := registry.New()
	now := time.Now()
	// i1 has Grasshopper loaded with two definitions; i2 has none (Grasshopper never loaded).
	reg.Register(&registry.Instance{InstanceID: "i1", PID: 7, RhinoVersion: "8.35", Platform: "macos", BridgeVersion: "dev",
		GrasshopperDocuments: []registry.GrasshopperDocument{
			{ID: "gh-abc", Title: "Tower.gh", Path: "/t.gh", Active: true, Enabled: true, ComponentCount: 42},
			{ID: "gh-def", Title: "Untitled", Active: false, Enabled: false, ComponentCount: 0},
		}}, 0, now)
	reg.Register(&registry.Instance{InstanceID: "i2", PID: 8, RhinoVersion: "8.35", Platform: "windows"}, 0, now)

	out := callListInstances(t, reg, now)
	byID := map[string]InstanceOut{}
	for _, i := range out.Instances {
		byID[i.InstanceID] = i
	}
	gh := byID["i1"].GrasshopperDocuments
	if len(gh) != 2 || gh[0].GrasshopperDocumentID != "gh-abc" || !gh[0].Active || !gh[0].Enabled || gh[0].ComponentCount != 42 || gh[0].Path != "/t.gh" {
		t.Fatalf("i1 gh_documents = %+v", gh)
	}
	if gh[1].Enabled || gh[1].Active || gh[1].ComponentCount != 0 {
		t.Fatalf("gh[1] = %+v", gh[1])
	}
	// A Grasshopper-less instance omits the field (nil -> omitempty on the wire).
	if byID["i2"].GrasshopperDocuments != nil {
		t.Fatalf("i2 should carry no gh_documents, got %+v", byID["i2"].GrasshopperDocuments)
	}
}

func TestSilentInstanceReadsUnresponsive(t *testing.T) {
	reg := registry.New()
	now := time.Now()
	reg.Register(&registry.Instance{InstanceID: "i1"}, 0, now)
	out := callListInstances(t, reg, now.Add(registry.UnresponsiveThreshold+time.Second))
	if out.Instances[0].Status != "unresponsive" {
		t.Fatalf("status = %s", out.Instances[0].Status)
	}
}
