//go:build harness

// Package harness_test is tier-2: live tests against a real, already-running
// Revit + MCP Bridge + MCP Server. Excluded from `go test ./...` by the
// "harness" build tag (skill's two-tier rule) -- run explicitly with
// `go test -tags harness ./revit/test-harness/... -run <Name>`.
//
// Assumes a Revit instance is already running and connected -- this suite
// does not launch or close Revit itself. A case that needs a connected
// instance and finds none SKIPs, it does not fail the suite; Revit/VM
// lifecycle automation is a separate concern (this session's launcher-agent
// work), not this harness's job.
package harness_test

import (
	"encoding/json"
	"flag"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/revit/test-harness/mcpclient"
)

var brokerExe = flag.String("broker-exe", os.Getenv("MCP_SERVER_EXE"), "path to the built mcp-server binary under test")

// startClient is the shared setup every case in this suite uses: launch the
// broker (as a secondary instance if one's already running and connected to
// Revit -- see the singleton lock-or-proxy design, PRD §05) and confirm at
// least one Revit instance is connected, skipping otherwise.
func startClient(t *testing.T) (*mcpclient.Client, listInstancesOut) {
	t.Helper()
	if *brokerExe == "" {
		t.Skip("no -broker-exe / MCP_SERVER_EXE set; nothing to test against")
	}

	c, err := mcpclient.Start(*brokerExe, "-mode", "local")
	if err != nil {
		t.Fatalf("start broker: %v", err)
	}
	t.Cleanup(func() { _ = c.Close() })

	raw, err := c.CallTool("list_instances", map[string]any{}, 10*time.Second)
	if err != nil {
		t.Fatalf("list_instances: %v", err)
	}
	out := decodeToolResult[listInstancesOut](t, raw)
	if len(out.Instances) == 0 {
		t.Skip("no Revit instance connected")
	}
	return c, out
}

type listInstancesOut struct {
	Instances []struct {
		InstanceID string `json:"instance_id"`
		Documents  []struct {
			DocumentID string `json:"document_id"`
			Title      string `json:"title"`
		} `json:"documents"`
	} `json:"instances"`
}

// toolResult mirrors the MCP tools/call envelope: text content plus, when
// the tool registers one, structuredContent carrying the typed payload.
type toolResult struct {
	StructuredContent json.RawMessage `json:"structuredContent"`
	IsError           bool            `json:"isError"`
}

func decodeToolResult[T any](t *testing.T, raw json.RawMessage) T {
	t.Helper()
	var tr toolResult
	if err := json.Unmarshal(raw, &tr); err != nil {
		t.Fatalf("decode tool envelope: %v\nraw: %s", err, raw)
	}
	var out T
	if err := json.Unmarshal(tr.StructuredContent, &out); err != nil {
		t.Fatalf("decode structuredContent: %v\nraw: %s", err, tr.StructuredContent)
	}
	return out
}

type executeScriptOut struct {
	ExecutionID string `json:"execution_id"`
	Status      string `json:"status"`
	Output      string `json:"output"`
}

// TestCreateLevel is this harness's first, most basic case: a real
// model-modifying write (Level.Create) succeeds through execute_script.
//
// The script reflects into RevitDocumentAdapter's private _document field
// to reach the real Autodesk.Revit.DB.Document rather than calling a
// sanctioned API -- because there isn't one yet. ScriptGlobals.Document is
// still IScriptDocument (Title only) as of this test; real Document access
// is the Phase 3 design this case exists to validate ahead of. Once that
// design ships, replace the reflection with the real accessor and this
// comment -- deliberately not hidden behind a helper, so it's impossible to
// miss when Phase 3 lands (see skill.md's "verify against the running
// connector, not the PRD" lesson).
func TestCreateLevel(t *testing.T) {
	c, instances := startClient(t)
	inst := instances.Instances[0]
	if len(inst.Documents) == 0 {
		t.Skip("connected instance has no open document")
	}
	doc := inst.Documents[0]

	script := `
var field = Document.GetType().GetField("_document", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
if (field == null) { return new { ok = false, stage = "reflect-field" }; }
var realDoc = (Autodesk.Revit.DB.Document)field.GetValue(Document);
var before = new Autodesk.Revit.DB.FilteredElementCollector(realDoc).OfClass(typeof(Autodesk.Revit.DB.Level)).GetElementCount();
var level = Autodesk.Revit.DB.Level.Create(realDoc, 999.0);
var after = new Autodesk.Revit.DB.FilteredElementCollector(realDoc).OfClass(typeof(Autodesk.Revit.DB.Level)).GetElementCount();
return new { ok = after == before + 1, levelId = level.Id.Value, before, after };
`

	raw, err := c.CallTool("execute_script", map[string]any{
		"instance_id": inst.InstanceID,
		"document_id": doc.DocumentID,
		"script":      script,
	}, 20*time.Second)
	if err != nil {
		t.Fatalf("execute_script: %v", err)
	}

	out := decodeToolResult[executeScriptOut](t, raw)
	if out.Status != "success" {
		t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
	}

	// Output is the script's anonymous object formatted via its default
	// ToString(), not JSON -- there's no typed contract for that shape to
	// decode against, so assert on the substring the script computed
	// (after == before + 1) rather than the exact source values, which
	// would make this test depend on how many levels the fixture document
	// happens to have today.
	if !strings.Contains(out.Output, "ok = True") {
		t.Fatalf("level was not created as expected; output: %s", out.Output)
	}
}
