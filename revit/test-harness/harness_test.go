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

var (
	brokerExe        = flag.String("broker-exe", os.Getenv("MCP_SERVER_EXE"), "path to the built mcp-server binary under test")
	brokerMode       = flag.String("broker-mode", envOr("MCP_SERVER_MODE", "local"), "topology to launch the broker in -- local or remote (PRD §05). MUST match whatever topology the real Revit instance is actually configured for: a mismatch doesn't error, it makes this process its own independent broker with zero connected instances, and every case below silently SKIPs rather than failing (the exact trap this flag exists to make loud instead of quiet)")
	brokerBind       = flag.String("broker-bind", envOr("MCP_SERVER_BIND", ""), "remote mode only: non-loopback bind address (PRD §05) -- required when -broker-mode=remote")
	brokerAppDataDir = flag.String("broker-app-data-dir", envOr("MCP_SERVER_APPDATA", ""), "remote mode: required, the shared-drive broker.json directory matching the real broker's own -app-data-dir. local mode: optional override")
)

func envOr(key, fallback string) string {
	if v, ok := os.LookupEnv(key); ok {
		return v
	}
	return fallback
}

// startClient is the shared setup every case in this suite uses: launch the
// broker (as a secondary instance if one's already running and connected to
// Revit -- see the singleton lock-or-proxy design, PRD §05) and confirm at
// least one Revit instance is connected, skipping otherwise.
func startClient(t *testing.T) (*mcpclient.Client, listInstancesOut) {
	t.Helper()
	if *brokerExe == "" {
		t.Skip("no -broker-exe / MCP_SERVER_EXE set; nothing to test against")
	}

	args := []string{"-mode", *brokerMode}
	switch *brokerMode {
	case "remote":
		if *brokerBind == "" || *brokerAppDataDir == "" {
			t.Fatalf("-broker-mode=remote requires -broker-bind and -broker-app-data-dir (or MCP_SERVER_BIND/MCP_SERVER_APPDATA) -- without them this would either fail to start or silently target the wrong broker.json location")
		}
		args = append(args, "-bind", *brokerBind, "-app-data-dir", *brokerAppDataDir)
	case "local":
		if *brokerAppDataDir != "" {
			args = append(args, "-app-data-dir", *brokerAppDataDir)
		}
	default:
		t.Fatalf("-broker-mode %q must be \"local\" or \"remote\"", *brokerMode)
	}

	c, err := mcpclient.Start(*brokerExe, args...)
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

// toolResult mirrors the MCP tools/call envelope: text content (always
// present, and the only place the actual PRD §01 diagnostic record shows up
// on failure) plus, on success, structuredContent carrying the typed
// payload. structuredContent is absent on error, so IsError must be checked
// BEFORE attempting to decode it -- a harness whose whole purpose is
// surfacing real failures must not itself reduce one to an opaque
// "unexpected end of JSON input".
type toolResult struct {
	Content []struct {
		Type string `json:"type"`
		Text string `json:"text"`
	} `json:"content"`
	StructuredContent json.RawMessage `json:"structuredContent"`
	IsError           bool            `json:"isError"`
}

func decodeToolResult[T any](t *testing.T, raw json.RawMessage) T {
	t.Helper()
	var tr toolResult
	if err := json.Unmarshal(raw, &tr); err != nil {
		t.Fatalf("decode tool envelope: %v\nraw: %s", err, raw)
	}
	if tr.IsError {
		text := "(no content)"
		if len(tr.Content) > 0 {
			text = tr.Content[0].Text
		}
		t.Fatalf("tool call returned an error: %s", text)
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
	// The PRD §01 diagnostic record for a failed script -- where a compile
	// error, an await rejection, or a ScriptApiDenylist violation actually
	// surfaces. Absent on success.
	Error *struct {
		Severity string `json:"severity"`
		Code     string `json:"code"`
		Source   string `json:"source"`
		Message  string `json:"message"`
		Remedy   string `json:"remedy"`
	} `json:"error"`
}

// failureText is everything a failed execution said, for assertions that
// don't want to care whether a given detail landed in the diagnostic
// record's message or in the script's own captured output.
func (o executeScriptOut) failureText() string {
	text := o.Output
	if o.Error != nil {
		text += " | " + o.Error.Code + " | " + o.Error.Message + " | " + o.Error.Remedy
	}
	return text
}

// targetDocument returns a connected instance and one open document, or
// skips -- the shared preamble for every case below.
func targetDocument(t *testing.T) (*mcpclient.Client, string, string) {
	t.Helper()
	c, instances := startClient(t)
	inst := instances.Instances[0]
	if len(inst.Documents) == 0 {
		t.Skip("connected instance has no open document")
	}
	return c, inst.InstanceID, inst.Documents[0].DocumentID
}

// runScript executes one script and returns its decoded result, failing on
// transport errors but NOT on a failed script status -- several cases below
// assert specifically that a script is rejected.
func runScript(t *testing.T, c *mcpclient.Client, instanceID, documentID, script string) executeScriptOut {
	t.Helper()
	raw, err := c.CallTool("execute_script", map[string]any{
		"instance_id": instanceID,
		"document_id": documentID,
		"script":      script,
	}, 20*time.Second)
	if err != nil {
		t.Fatalf("execute_script: %v", err)
	}
	return decodeToolResult[executeScriptOut](t, raw)
}

// TestCreateLevel is this harness's first, most basic case: a real
// model-modifying write (Level.Create) succeeds through execute_script.
//
// As of PRD §14 (Phase 3) this uses the SANCTIONED `Document` global, which
// is the real Autodesk.Revit.DB.Document. It previously reflected into
// RevitDocumentAdapter's private _document field because no sanctioned
// accessor existed; that workaround is gone along with the narrow
// IScriptDocument seam it worked around.
func TestCreateLevel(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	script := `
var before = new Autodesk.Revit.DB.FilteredElementCollector(Document).OfClass(typeof(Autodesk.Revit.DB.Level)).GetElementCount();
var level = Autodesk.Revit.DB.Level.Create(Document, 999.0);
var after = new Autodesk.Revit.DB.FilteredElementCollector(Document).OfClass(typeof(Autodesk.Revit.DB.Level)).GetElementCount();
return new { ok = after == before + 1, levelId = level.Id.Value, before, after };
`

	out := runScript(t, c, instanceID, documentID, script)
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

// TestScriptGlobalsExposeRealRevitObjects covers what MCPBridge.Core.Tests
// no longer can. Those assertions (a script reading Document.Title and
// getting the document's real title back) used to run against
// FakeDocumentAdapter in tier 1; once `Document` became the real
// Autodesk.Revit.DB.Document that stopped being fakeable at all -- Document
// is sealed and non-constructible outside a live Revit session, and
// RevitAPI.dll is a mixed-mode assembly a test host cannot even load. So
// this is the tier that can still make them (PRD §14).
func TestScriptGlobalsExposeRealRevitObjects(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	// Each global is asserted to be the REAL Revit type, by name, not merely
	// non-null: a narrow adapter would also answer .Title, so the type check
	// is what actually distinguishes the Phase 3 seam from the old one.
	script := `
return new {
  docType = Document.GetType().FullName,
  uiAppType = UIApplication.GetType().FullName,
  uiDocType = UIDocument == null ? "null" : UIDocument.GetType().FullName,
  title = Document.Title,
  sameDoc = object.ReferenceEquals(Document, UIDocument.Document)
};
`

	out := runScript(t, c, instanceID, documentID, script)
	if out.Status != "success" {
		t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
	}
	for _, want := range []string{
		"docType = Autodesk.Revit.DB.Document",
		"uiAppType = Autodesk.Revit.UI.UIApplication",
		"uiDocType = Autodesk.Revit.UI.UIDocument",
	} {
		if !strings.Contains(out.Output, want) {
			t.Errorf("globals do not expose the real Revit types: wanted %q in output: %s", want, out.Output)
		}
	}
}

// TestDenylistRejectsOwnTransaction is the live half of the ScriptApiDenylist
// coverage. Tier 1 proves the check fires and that the executor rolls back;
// only here can we prove the whole path -- that a rejected script comes back
// to the agent as a clear, named diagnostic rather than a crash, a hang, or
// (worst) a silent no-op that leaves the ambient transaction in an odd state
// and the next script failing for an unrelated-looking reason.
func TestDenylistRejectsOwnTransaction(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	for _, tc := range []struct{ name, script string }{
		{"Transaction", `using (var tx = new Autodesk.Revit.DB.Transaction(Document, "mine")) { tx.Start(); tx.Commit(); } return "ran";`},
		{"TransactionGroup", `using (var tg = new Autodesk.Revit.DB.TransactionGroup(Document, "mine")) { tg.Start(); tg.Assimilate(); } return "ran";`},
		{"SubTransaction", `using (var st = new Autodesk.Revit.DB.SubTransaction(Document)) { st.Start(); st.Commit(); } return "ran";`},
		{"Document.Close", `Document.Close(); return "ran";`},
		{"Document.SaveAs", `Document.SaveAs("C:\\Temp\\should-never-happen.rvt"); return "ran";`},
	} {
		t.Run(tc.name, func(t *testing.T) {
			out := runScript(t, c, instanceID, documentID, tc.script)
			if out.Status == "success" {
				t.Fatalf("denylisted script was allowed to run; output: %s", out.Output)
			}
			if !strings.Contains(out.failureText(), "script-api-denied") {
				t.Errorf("rejection does not name the denylist code; result: %s", out.failureText())
			}
		})
	}

	// The instance must still be usable afterwards -- a rejection happens
	// before compilation completes, so the ambient transaction is rolled
	// back cleanly and the next script runs normally.
	out := runScript(t, c, instanceID, documentID, `return Document.Title;`)
	if out.Status != "success" {
		t.Fatalf("instance unusable after denylist rejections: status=%q output=%s", out.Status, out.Output)
	}
}
