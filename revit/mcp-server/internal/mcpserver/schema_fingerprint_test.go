package mcpserver

import (
	"context"
	"testing"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/discovery"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/execution"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/registry"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/semsearch/manager"
	"github.com/google/jsonschema-go/jsonschema"
	"github.com/modelcontextprotocol/go-sdk/mcp"
)

func TestToolSchemaFingerprintIsStableAndHex(t *testing.T) {
	a, err := ToolSchemaFingerprint()
	if err != nil {
		t.Fatal(err)
	}
	b, err := ToolSchemaFingerprint()
	if err != nil {
		t.Fatal(err)
	}
	if a != b {
		t.Fatalf("fingerprint not stable: %q vs %q", a, b)
	}
	if len(a) != 64 {
		t.Fatalf("fingerprint = %q, want 64 hex chars (sha256)", a)
	}
}

// The fingerprint must actually depend on the tool contract: a changed input
// schema, or a changed tool-name set, must change the hash. Proven by folding
// one synthetic extra tool into the same hashing the real function does and
// showing the result differs -- without this, a fingerprint that ignored its
// input (e.g. a constant) would pass every other test here.
func TestToolSchemaFingerprintDependsOnTheContract(t *testing.T) {
	base, err := ToolSchemaFingerprint()
	if err != nil {
		t.Fatal(err)
	}
	saved := toolInputTypes
	defer func() { toolInputTypes = saved }()
	// (a) The tool-name SET feeds the hash: an added tool changes it.
	toolInputTypes = append(append([]struct {
		name   string
		schema func() (*jsonschema.Schema, error)
	}{}, saved...), struct {
		name   string
		schema func() (*jsonschema.Schema, error)
	}{"synthetic_extra_tool", schemaFor[ExecuteScriptIn]})
	if added, err := ToolSchemaFingerprint(); err != nil {
		t.Fatal(err)
	} else if added == base {
		t.Fatal("adding a tool did not change the fingerprint")
	}

	// (b) Each tool's input SCHEMA feeds the hash, not just its name: swapping
	// one tool's argument type for a different shape (the same class of change
	// as adding a `label` field to execute_script, which is what issue #197 is
	// about) must change the fingerprint even though the tool-name set is
	// identical.
	toolInputTypes = append([]struct {
		name   string
		schema func() (*jsonschema.Schema, error)
	}{}, saved...)
	toolInputTypes[0].schema = schemaFor[PollExecutionIn] // execute_script's args, but a different shape
	if swapped, err := ToolSchemaFingerprint(); err != nil {
		t.Fatal(err)
	} else if swapped == base {
		t.Fatal("changing a tool's input schema (same name) did not change the fingerprint")
	}
}

// Drift guard: the fingerprint's tool list must match the tools the broker
// actually registers, or a secondary would wave through (or wrongly refuse)
// over a contract that omits a real tool. Stands up the real server exactly as
// runPrimary does -- registration needs no models or Revit, only the handler
// closures' deps, which can be empty here -- and asserts name-set parity.
func TestSchemaFingerprintCoversEveryRegisteredTool(t *testing.T) {
	s := mcp.NewServer(&mcp.Implementation{Name: "test", Version: "test"}, nil)
	execMgr := execution.NewManager()
	reg := registry.New()
	router := discovery.NewRouter(reg)
	search := manager.New(router, nil, nil, func(string, ...any) {})

	Register(s, execMgr)
	RegisterDiscovery(s, router, search)
	RegisterInstances(s, reg, execMgr)
	RegisterHowTo(s, HowToDeps{Registry: reg, Router: router, Exec: execMgr, Version: "test"})
	RegisterSkills(s, "test")
	RegisterUpdate(s, UpdateDeps{Mode: "local", Version: "test"})

	ct, st := mcp.NewInMemoryTransports()
	ctx := context.Background()
	ss, err := s.Connect(ctx, st, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer ss.Close()
	client := mcp.NewClient(&mcp.Implementation{Name: "probe", Version: "test"}, nil)
	cs, err := client.Connect(ctx, ct, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer cs.Close()

	res, err := cs.ListTools(ctx, nil)
	if err != nil {
		t.Fatal(err)
	}
	advertised := map[string]bool{}
	for _, tool := range res.Tools {
		advertised[tool.Name] = true
	}
	declared := fingerprintedToolNames()

	for name := range advertised {
		if !declared[name] {
			t.Errorf("tool %q is registered but missing from toolInputTypes (fingerprint would ignore its schema)", name)
		}
	}
	for name := range declared {
		if !advertised[name] {
			t.Errorf("tool %q is in toolInputTypes but not registered by the server", name)
		}
	}

	// The skill-file test keeps its own literal tool list; it and the
	// fingerprint list must name the same tools, or one of the two guards is
	// checking a stale set.
	skillList := map[string]bool{}
	for _, n := range registeredToolNames {
		skillList[n] = true
	}
	for name := range declared {
		if !skillList[name] {
			t.Errorf("tool %q is fingerprinted but missing from skills_tool_test.go's registeredToolNames", name)
		}
	}
	for name := range skillList {
		if !declared[name] {
			t.Errorf("tool %q is in skills_tool_test.go's registeredToolNames but not fingerprinted", name)
		}
	}
}
