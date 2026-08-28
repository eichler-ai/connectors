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
	"strconv"
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
	// Notices carries the PRD §01 diagnostic records a run reports alongside
	// its result — read here because issue #24's partial-commit reporting has
	// no other observable: the run's status only says "failed", and which
	// documents kept their changes lives in a notice, not in the status.
	Notices []struct {
		Severity string   `json:"severity"`
		Code     string   `json:"code"`
		Source   string   `json:"source"`
		Message  string   `json:"message"`
		Remedy   []string `json:"remedy"`
	} `json:"notices"`
	Error *struct {
		Code    string `json:"code"`
		Message string `json:"message"`
	} `json:"error"`
}

// Note there is deliberately no Error field here. A script that fails to
// compile (including a ScriptApiDenylist rejection) does not come back as a
// successful tool call carrying an error record in structuredContent -- the
// whole tool call is an MCP error and the PRD §01 record arrives as its text
// content instead. See runRejectedScript, which is what reads it.

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

// runScript executes one script that is expected to SUCCEED.
func runScript(t *testing.T, c *mcpclient.Client, instanceID, documentID, script string) executeScriptOut {
	t.Helper()
	return decodeToolResult[executeScriptOut](t, callExecuteScript(t, c, instanceID, documentID, script))
}

// rejectedScript is the PRD §01 diagnostic record a refused execution comes
// back as. `code` and `remedy` are read as fields here, deliberately: they were
// both wrong on the wire while every message-substring assertion in this file
// passed -- every failure reported code:"script-execution-failed" and no remedy
// at all, so the codes skill.md tells an agent to match on existed only inside
// the prose. A test that greps the message cannot see that.
type rejectedScript struct {
	Text string
	// Output is the failed run's stdout. Present because a script that throws
	// still reports what it printed before throwing, and that is the only way
	// to learn something (here, a created document's title) from a run whose
	// return value is gone -- which in turn keeps the follow-up assertion
	// scoped to one document instead of scanning every open one.
	Output string `json:"output"`
	Error  struct {
		Code    string   `json:"code"`
		Message string   `json:"message"`
		Remedy  []string `json:"remedy"`
	} `json:"error"`
}

// runRejectedScript executes one script that is expected to be REJECTED, and
// returns everything the connector said about why.
//
// A rejected script does not come back as a successful tool call carrying
// status:"failed" -- it comes back as an MCP tool ERROR whose text content is
// the PRD §01 diagnostic record. That is why this cannot go through
// decodeToolResult, which fails the test on isError by design -- correctly, for
// every case that expects a result.
func runRejectedScript(t *testing.T, c *mcpclient.Client, instanceID, documentID, script string) rejectedScript {
	t.Helper()
	return rejectionOf(t, callExecuteScript(t, c, instanceID, documentID, script))
}

func rejectionOf(t *testing.T, raw json.RawMessage) rejectedScript {
	t.Helper()
	var tr toolResult
	if err := json.Unmarshal(raw, &tr); err != nil {
		t.Fatalf("decode tool envelope: %v\nraw: %s", err, raw)
	}
	if !tr.IsError {
		t.Fatalf("script was expected to be rejected but the call succeeded: %s", raw)
	}
	if len(tr.Content) == 0 {
		t.Fatalf("rejection carried no content at all, so nothing tells an agent what happened: %s", raw)
	}

	out := rejectedScript{Text: tr.Content[0].Text}
	if err := json.Unmarshal([]byte(tr.Content[0].Text), &out); err != nil {
		t.Fatalf("rejection text is not the PRD §01 record it is supposed to be: %v\ntext: %s", err, tr.Content[0].Text)
	}
	return out
}

func callExecuteScript(t *testing.T, c *mcpclient.Client, instanceID, documentID, script string) json.RawMessage {
	t.Helper()
	return callExecuteScriptWith(t, c, instanceID, documentID, script, nil)
}

// callExecuteScriptWith is callExecuteScript plus any extra tool arguments —
// today only confirm_lifecycle_actions (PRD §14). Kept as a separate helper so
// every existing case keeps sending the exact argument set it sent before:
// the confirmation gate's whole contract is that a request WITHOUT the flag is
// refused, and a helper that started passing it by default would quietly
// dismantle that.
func callExecuteScriptWith(t *testing.T, c *mcpclient.Client, instanceID, documentID, script string, extra map[string]any) json.RawMessage {
	t.Helper()
	args := map[string]any{
		"instance_id": instanceID,
		"document_id": documentID,
		"script":      script,
	}
	for k, v := range extra {
		args[k] = v
	}
	// LONGER THAN THE SERVER'S OWN default timeout_ms (30s, mcpserver.defaultTimeoutMs),
	// deliberately. At 20s the client gave up BEFORE the broker would have answered, so a
	// script that legitimately ran 20-30s -- creating Revit documents is genuinely slow, and
	// slower as a session accumulates them -- failed here while the add-in carried on running
	// it, and every subsequent call in the suite came back "busy". That reads like a hung
	// script and is really just a client deadline shorter than the server's. Keep this above
	// defaultTimeoutMs so the connector's own pending/running contract is what decides.
	raw, err := c.CallTool("execute_script", args, 45*time.Second)
	if err != nil {
		t.Fatalf("execute_script: %v", err)
	}
	return raw
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

	for _, tc := range []struct{ name, member, script string }{
		{"Transaction", "Autodesk.Revit.DB.Transaction", `using (var tx = new Autodesk.Revit.DB.Transaction(Document, "mine")) { tx.Start(); tx.Commit(); } return "ran";`},
		{"TransactionGroup", "Autodesk.Revit.DB.TransactionGroup", `using (var tg = new Autodesk.Revit.DB.TransactionGroup(Document, "mine")) { tg.Start(); tg.Assimilate(); } return "ran";`},
		{"SubTransaction", "Autodesk.Revit.DB.SubTransaction", `using (var st = new Autodesk.Revit.DB.SubTransaction(Document)) { st.Start(); st.Commit(); } return "ran";`},
	} {
		t.Run(tc.name, func(t *testing.T) {
			rej := runRejectedScript(t, c, instanceID, documentID, tc.script)
			// The code an agent keys off, and the member it actually used --
			// a rejection that named neither would be unactionable (PRD §01).
			if rej.Error.Code != "script-api-denied" {
				t.Errorf("record's code = %q, want script-api-denied; result: %s", rej.Error.Code, rej.Text)
			}
			if !strings.Contains(rej.Text, tc.member) {
				t.Errorf("rejection does not name %q, so an agent cannot tell what it did wrong; result: %s", tc.member, rej.Text)
			}
			if len(rej.Error.Remedy) == 0 {
				t.Errorf("rejection carries no remedy, though there is a concrete next step; result: %s", rej.Text)
			}
		})
	}

	// A `dynamic` argument makes constructor overload resolution late-bound,
	// which an independent PR review flagged as a way past a check keyed on the
	// bound symbol. Live, it is not: this is refused like any other spelling of
	// `new` (Roslyn still reports the constructor symbol, and Analyze now falls
	// back to the constructed TYPE if it ever stops doing so). Pinned live as
	// well as in tier 1 because check 1 is the one refusal that can never be
	// opted into, and `dynamic` genuinely works in scripts here.
	t.Run("LateBoundConstructorArgument", func(t *testing.T) {
		rej := runRejectedScript(t, c, instanceID, documentID,
			`dynamic d = Document; var tx = new Autodesk.Revit.DB.Transaction(d, "mine"); return "constructed";`)
		if rej.Error.Code != "script-api-denied" {
			t.Errorf("record's code = %q, want script-api-denied; result: %s", rej.Error.Code, rej.Text)
		}
	})

	// Confirmation is for the lifecycle members only; it must not become a
	// general "ignore the denylist" switch. Live-checked because that is the
	// one place a broker-side wiring mistake (forwarding the flag into some
	// broader allow) would actually show up.
	t.Run("ConfirmationDoesNotUnlockTransactions", func(t *testing.T) {
		raw := callExecuteScriptWith(t, c, instanceID, documentID,
			`using (var tx = new Autodesk.Revit.DB.Transaction(Document, "mine")) { tx.Start(); tx.Commit(); } return "ran";`,
			map[string]any{"confirm_lifecycle_actions": true})
		var tr toolResult
		if err := json.Unmarshal(raw, &tr); err != nil {
			t.Fatalf("decode tool envelope: %v\nraw: %s", err, raw)
		}
		if !tr.IsError {
			t.Fatalf("confirm_lifecycle_actions let a script open its own Transaction: %s", raw)
		}
		if len(tr.Content) == 0 || !strings.Contains(tr.Content[0].Text, "script-api-denied") {
			t.Errorf("expected the unconditional script-api-denied rejection; result: %s", raw)
		}
	})

	// The instance must still be usable afterwards -- a rejection happens
	// before compilation completes, so the ambient transaction is rolled
	// back cleanly and the next script runs normally.
	out := runScript(t, c, instanceID, documentID, `return Document.Title;`)
	if out.Status != "success" {
		t.Fatalf("instance unusable after denylist rejections: status=%q output=%s", out.Status, out.Output)
	}
}

// TestLifecycleGateRequiresConfirmation is the live half of PRD §14's
// confirmation gate: the same script text must be REFUSED without
// confirm_lifecycle_actions and RUN with it, end to end through the real
// broker and a real Revit.
//
// It has to be here rather than in MCPBridge.Core.Tests for the same reason
// the denylist case above does -- tier 1 can prove the gate's decision but
// cannot execute a script that names Revit types at all (RevitAPI.dll is
// mixed-mode and won't load in a test host), so "and then it actually ran" is
// only assertable against a live Revit.
//
// WHY A METHOD GROUP RATHER THAN A CALL. The confirmed script binds
// Document.Close to a delegate and never invokes it. That is deliberate, and
// it is not a weaker test of the gate: the gate's job is to decide whether the
// compiled script may proceed to execution, and detection fires on the method
// group exactly as it does on a call (that bypass shape is pinned in tier 1
// too). Actually invoking one of these members live would close, save, print,
// or relinquish a real document on a real machine -- effects that, by the very
// definition that put them behind this gate, nothing here could undo
// afterwards. A regression suite must not need a human to repair the model it
// ran against.
func TestLifecycleGateRequiresConfirmation(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	const script = `System.Func<bool> close = Document.Close; return close != null ? "bound" : "null";`

	// Unconfirmed: refused, before anything runs.
	rej := runRejectedScript(t, c, instanceID, documentID, script)
	if rej.Error.Code != "script-lifecycle-confirmation-required" {
		t.Errorf("record's code = %q, want script-lifecycle-confirmation-required -- an agent matching on the field, as skill.md tells it to, cannot see this is retryable; result: %s", rej.Error.Code, rej.Text)
	}
	if !strings.Contains(strings.Join(rej.Error.Remedy, " "), "confirm_lifecycle_actions: true") {
		t.Errorf("remedy does not tell the agent to resend with confirm_lifecycle_actions, which is the whole point of a retryable refusal (PRD §01); result: %s", rej.Text)
	}
	if !strings.Contains(rej.Text, "Autodesk.Revit.DB.Document.Close") {
		t.Errorf("refusal does not name the member the script used; result: %s", rej.Text)
	}

	// Confirmed: the identical text runs.
	out := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID, script,
		map[string]any{"confirm_lifecycle_actions": true}))
	if out.Status != "success" {
		t.Fatalf("confirmed lifecycle script did not run: status=%q output=%s", out.Status, out.Output)
	}
	if !strings.Contains(out.Output, "bound") {
		t.Errorf("confirmed script ran but did not return its own result; output=%s", out.Output)
	}

	// And the confirmation is per-request, not sticky: the same text, resent
	// without the flag, is refused again. A cached-compilation implementation
	// that folded the decision into the compile step would pass every
	// assertion above and fail this one.
	again := runRejectedScript(t, c, instanceID, documentID, script)
	if again.Error.Code != "script-lifecycle-confirmation-required" {
		t.Errorf("confirmation leaked to a later unconfirmed run of the same script; result: %s", again.Text)
	}
}

// TestLifecycleGateCoversTheNewlyAddedMembers checks the five members added to
// the gated tier after an independent PR review proposed them (PRD §14):
// Document.SaveAsCloudModel/Dispose, PrintManager.SubmitPrint,
// UIDocument.SaveAndClose, UIApplication.PostCommand. Each exists with these
// signatures in the live Revit 2027 API (verified with describe_function before
// being added), and each answers "no" to the list's membership question -- a
// thrown exception undoes none of them.
//
// Every script here binds a METHOD GROUP and never invokes it, for the same
// reason TestLifecycleGateRequiresConfirmation does: detection fires on the
// reference exactly as on a call, and actually invoking any of these would
// close, save, print, or cloud-publish a real model on a real machine, which
// nothing in a regression suite could undo afterwards.
func TestLifecycleGateCoversTheNewlyAddedMembers(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	for _, tc := range []struct{ name, member, script string }{
		{"SaveAsCloudModel", "Autodesk.Revit.DB.Document.SaveAsCloudModel",
			`System.Action<System.Guid, System.Guid, string, string> f = Document.SaveAsCloudModel; return f != null ? "bound" : "null";`},
		{"Dispose", "Autodesk.Revit.DB.Document.Dispose",
			`System.Action f = Document.Dispose; return f != null ? "bound" : "null";`},
		{"SubmitPrint", "Autodesk.Revit.DB.PrintManager.SubmitPrint",
			`System.Func<bool> f = Document.PrintManager.SubmitPrint; return f != null ? "bound" : "null";`},
		{"SaveAndClose", "Autodesk.Revit.UI.UIDocument.SaveAndClose",
			`System.Func<bool> f = UIDocument.SaveAndClose; return f != null ? "bound" : "null";`},
		{"PostCommand", "Autodesk.Revit.UI.UIApplication.PostCommand",
			`System.Action<Autodesk.Revit.UI.RevitCommandId> f = UIApplication.PostCommand; return f != null ? "bound" : "null";`},
	} {
		t.Run(tc.name, func(t *testing.T) {
			rej := runRejectedScript(t, c, instanceID, documentID, tc.script)
			if rej.Error.Code != "script-lifecycle-confirmation-required" {
				t.Errorf("record's code = %q, want script-lifecycle-confirmation-required; result: %s", rej.Error.Code, rej.Text)
			}
			if !strings.Contains(rej.Text, tc.member) {
				t.Errorf("refusal does not name %q; result: %s", tc.member, rej.Text)
			}

			// The same text, confirmed, gets through the gate and runs.
			out := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID, tc.script,
				map[string]any{"confirm_lifecycle_actions": true}))
			if out.Status != "success" || !strings.Contains(out.Output, "bound") {
				t.Fatalf("confirmed script did not run: status=%q output=%s", out.Status, out.Output)
			}
		})
	}
}

// TestApplicationCreatesDocuments covers the top-level
// Autodesk.Revit.ApplicationServices.Application object -- reached from a script
// as UIApplication.Application -- and specifically the two document-creating
// members the PRD §13 corpus's fixture system needs: NewProjectDocument and
// NewFamilyDocument.
//
// WHY THIS EXISTS AS A TEST RATHER THAN AS A FEATURE. Exposing Application was
// planned as a fast-follow to PRD §14, on the assumption that ScriptGlobals would
// need a fourth delegating property and a fourth IRaw*Source capability interface.
// It needs none: PRD §14 made UIApplication the REAL Autodesk.Revit.UI.UIApplication,
// and .Application is an ordinary property on that real type, so the whole
// ApplicationServices.Application surface has been reachable since §14 shipped. No
// code was written for this; the fast-follow reduced to proving the capability is
// real and writing it down (PRD §14, "Application-level access needed no new
// plumbing"). That makes a live assertion the only thing standing between the claim
// and silent regression -- exactly the tier-2 case this harness is for.
//
// NEITHER MEMBER IS CONFIRMATION-GATED, and every subtest below runs WITHOUT
// confirm_lifecycle_actions, which is what pins that. Run against
// ScriptApiDenylist's own membership question -- "does a thrown exception in the
// script actually undo this?" -- a freshly created, unsaved, in-memory document has
// nothing to undo: it touches no file, no central model, no device, and no document
// a human has open. What WOULD escape the rollback boundary is persisting it, and
// Document.Save/SaveAs/SaveAsCloudModel are already gated in their own right. So
// creation stays unrestricted and the gate sits exactly where the effect becomes
// irreversible, not one step earlier.
//
// SESSION COST, deliberately accepted: every run leaves its documents open in the
// live Revit session (nothing here closes them -- Document.Close is gated, and
// Dispose with it). That matches the corpus plan's own position: fresh throwaway
// documents accumulate in memory and are reclaimed by restarting Revit between full
// runs, not by inventing a cleanup mechanism.
func TestApplicationCreatesDocuments(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	// The real type, by name, plus the two members -- the same shape
	// TestScriptGlobalsExposeRealRevitObjects uses for the §14 globals, for the
	// same reason: a wrapper would also answer a call, so the type check is what
	// distinguishes reaching the real Application from reaching something like it.
	t.Run("ApplicationIsTheRealRevitType", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, `
var app = UIApplication.Application;
return new {
  appType = app.GetType().FullName,
  version = app.VersionNumber,
  projectTemplate = app.DefaultProjectTemplate,
  // Null-coalesced: DefaultProjectTemplate is null on an install that never
  // configured one, and a bare .Length would then throw a
  // NullReferenceException the agent only sees as an opaque
  // "expected status=success" instead of the clear skip below.
  projectTemplateUsable = (app.DefaultProjectTemplate ?? "").Length > 0 && System.IO.File.Exists(app.DefaultProjectTemplate ?? "")
};
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
		}
		// Matched with the trailing comma so this cannot be satisfied by some
		// longer type name that merely starts the same way.
		if !strings.Contains(out.Output, "appType = Autodesk.Revit.ApplicationServices.Application,") {
			t.Fatalf("UIApplication.Application is not the real Application type; output: %s", out.Output)
		}
		// DefaultProjectTemplate is what the three subtests below build on, and it
		// is per-install: it can be blank, or name a template this machine never
		// shipped. Checked here so that shows up once, clearly, rather than three
		// times as an opaque "expected status=success" from whichever script tried
		// to use it. SKIPped rather than failed, for the same reason
		// NewFamilyDocument skips on a missing family template: a Revit install
		// without a usable default project template is an environment precondition
		// this harness does not own, not a regression in the capability under test.
		if !strings.Contains(out.Output, "projectTemplateUsable = True") {
			t.Skipf("Application.DefaultProjectTemplate does not name a file that exists on this machine; output: %s", out.Output)
		}
	})

	// The fixture-system helper the corpus plan needs: a blank document from
	// Revit's own shipped default template, so no fixture asset has to be
	// committed to this repo. Asserts the document is real and queryable
	// (levels come from the template), not merely non-null.
	t.Run("NewProjectDocument", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, `
var app = UIApplication.Application;
var doc = app.NewProjectDocument(app.DefaultProjectTemplate);
var levels = new Autodesk.Revit.DB.FilteredElementCollector(doc)
    .OfClass(typeof(Autodesk.Revit.DB.Level)).GetElementCount();
return new {
  docType = doc.GetType().FullName,
  isFamily = doc.IsFamilyDocument,
  unsaved = doc.PathName.Length == 0,
  hasLevels = levels > 0
};
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
		}
		for _, want := range []string{
			"docType = Autodesk.Revit.DB.Document",
			"isFamily = False",
			"unsaved = True",
			"hasLevels = True",
		} {
			if !strings.Contains(out.Output, want) {
				t.Errorf("wanted %q in output: %s", want, out.Output)
			}
		}
	})

	// THE BOUNDARY THAT ACTUALLY CONSTRAINS THE FIXTURE SYSTEM, pinned here so it
	// cannot be rediscovered the hard way. TransactionScriptExecutor's ambient
	// Transaction is opened on the ACTIVE document; a document this script just
	// created is not that document and is not covered by it, so writing to it
	// throws ModificationOutsideTransactionException. Revit itself is fine with a
	// second Transaction on a different document (one-open-transaction is a
	// per-document rule) -- it is ScriptApiDenylist check 1 that refuses to
	// construct one, unconditionally and without regard to which document it
	// targets. Closing that gap is a separate, deliberate piece of work, tracked
	// as issue #24 -- and its chosen fix does NOT narrow check 1: the executor
	// will auto-wrap every document a script creates in its own managed
	// transaction, so the refusal stays unconditional. Until that lands, a script
	// can CREATE a blank document and READ it, not write to it.
	t.Run("NewDocumentIsOutsideTheAmbientTransaction", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, `
var app = UIApplication.Application;
var doc = app.NewProjectDocument(app.DefaultProjectTemplate);
try {
  Autodesk.Revit.DB.Level.Create(doc, 123.0);
  return "modified";
} catch (Autodesk.Revit.Exceptions.ModificationOutsideTransactionException) {
  return "outside-transaction";
}
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
		}
		if !strings.Contains(out.Output, "outside-transaction") {
			t.Fatalf("a freshly created document was writable without its own transaction -- if that is now genuinely true, this test and the corpus plan's fixture design both need revisiting; output: %s", out.Output)
		}
	})

	// HOW A FIXTURE DOCUMENT IS ADDRESSED ACROSS CALLS, which is the question the
	// corpus plan left open. The answer is NOT a document_id: a document created
	// this way never appears in list_instances (register's document list is a
	// one-shot snapshot taken at connect, PRD §05) and execute_script does not
	// route by document_id anyway -- every script runs against ActiveUIDocument
	// (RequestDispatcher's own KNOWN LIMITATION comment). A script also cannot
	// change that -- see CannotChangeTheActiveDocumentFromAScript below.
	// What DOES work is in-script addressing: the document stays in
	// Application.Documents for the rest of the session, so a later script finds it
	// there by title. That is the mechanism, and this pins it.
	t.Run("CreatedDocumentStaysInApplicationDocuments", func(t *testing.T) {
		created := runScript(t, c, instanceID, documentID, `
var app = UIApplication.Application;
return app.NewProjectDocument(app.DefaultProjectTemplate).Title;
`)
		if created.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", created.Status, created.Output)
		}
		title := strings.TrimSpace(created.Output)
		if title == "" {
			t.Fatalf("created document reported no title, so nothing can address it later")
		}

		// strconv.Quote, not a raw splice: a title carrying a quote, a backslash or
		// a newline would otherwise produce a C# syntax error, and the failure
		// would reach the reader as an opaque "expected status=success" with
		// nothing pointing at the quoting. Revit's own ProjectN titles never do
		// this today; a document the operator saved under some other name could.
		//
		// The match is title AND unsaved, not title alone -- an unrelated open
		// document sharing the title would otherwise satisfy this subtest without
		// the created one having survived at all. Counted rather than
		// short-circuited so the failure message can say whether it found none or
		// found several.
		//
		// The count is TERMINATED with a semicolon, and the assertion matches the
		// terminated string: an unterminated "matches = 1" is a prefix of
		// "matches = 10", "matches = 11" and so on, so a substring check on it
		// could not tell one from several -- the exact distinction this subtest
		// exists to make, and a live one, since this case deliberately leaves its
		// documents open and repeated runs in one session accumulate them.
		found := runScript(t, c, instanceID, documentID, `
int matches = 0;
foreach (Autodesk.Revit.DB.Document d in UIApplication.Application.Documents) {
  if (d.Title == `+strconv.Quote(title)+` && d.PathName.Length == 0) { matches++; }
}
return "matches = " + matches + ";";
`)
		if found.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", found.Status, found.Output)
		}
		if !strings.Contains(found.Output, "matches = 1;") {
			t.Fatalf("expected exactly one unsaved open document titled %q -- it was created by an earlier execute_script call, so if it is gone, nothing can address a fixture document across calls; output: %s", title, found.Output)
		}
	})

	// The third limit, pinned rather than merely written down: a script cannot
	// make a created document the active one, so it cannot route around
	// execute_script targeting ActiveUIDocument. Attempted against the ACTIVE
	// document's OWN path deliberately -- that document is already open and
	// already active, so if the call unexpectedly succeeded it would be a no-op
	// rather than switching the session out from under a person.
	t.Run("CannotChangeTheActiveDocumentFromAScript", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, `
if (Document.PathName.Length == 0) { return "unsaved-active-document"; }
try {
  UIApplication.OpenAndActivateDocument(Document.PathName);
  return "activated";
} catch (Autodesk.Revit.Exceptions.InvalidOperationException ex) {
  // Autodesk.Revit.Exceptions.InvalidOperationException, NOT System's -- they
  // share a short name, so ex.GetType().Name reads identically in a probe and
  // catching the wrong one fails with the very message you were expecting.
  return "refused: " + ex.Message;
}
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
		}
		if strings.Contains(out.Output, "unsaved-active-document") {
			t.Skip("the active document has never been saved, so it has no path to re-activate by")
		}
		if !strings.Contains(out.Output, "refused:") {
			t.Fatalf("OpenAndActivateDocument was not refused from inside the ambient transaction; if that is now genuinely allowed, PRD §14's account of fixture addressing needs revisiting; output: %s", out.Output)
		}
	})

	// Phase D of the corpus plan (family editing) happens in a FAMILY document,
	// a different document context from every other phase. Asserted through
	// FamilyManager rather than just IsFamilyDocument, since that is the API the
	// parametric cases actually go through -- IsFamilyDocument alone would pass
	// against a document too degenerate to use.
	//
	// The template is discovered from Application.FamilyTemplatePath rather than
	// hardcoded: the path is per-machine and per-Revit-version, and a literal here
	// would turn "this VM installed a different language pack" into a failure of
	// the capability under test. If no Generic Model template is installed the
	// subtest skips, per this harness's standing rule about environment
	// preconditions it does not own.
	t.Run("NewFamilyDocument", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, `
var app = UIApplication.Application;
string template = "";
if (System.IO.Directory.Exists(app.FamilyTemplatePath)) {
  foreach (var f in System.IO.Directory.EnumerateFiles(app.FamilyTemplatePath, "Generic Model.rft", System.IO.SearchOption.AllDirectories)) {
    template = f;
    break;
  }
}
if (template.Length == 0) { return "no-template"; }
var doc = app.NewFamilyDocument(template);
return new {
  docType = doc.GetType().FullName,
  isFamily = doc.IsFamilyDocument,
  hasFamilyManager = doc.FamilyManager != null,
  hasTypes = doc.FamilyManager.Types != null
};
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
		}
		if strings.Contains(out.Output, "no-template") {
			t.Skip("no \"Generic Model.rft\" under Application.FamilyTemplatePath on this machine")
		}
		for _, want := range []string{
			"docType = Autodesk.Revit.DB.Document",
			"isFamily = True",
			"hasFamilyManager = True",
			"hasTypes = True",
		} {
			if !strings.Contains(out.Output, want) {
				t.Errorf("wanted %q in output: %s", want, out.Output)
			}
		}
	})
}

// TestCreatedDocumentIsWritable is issue #24's whole point, live: a script can
// create a document AND write to it AND have that write actually commit.
//
// WHAT CHANGED, and what deliberately did not. Before this, a script could
// create a document via UIApplication.Application.NewProjectDocument and read
// it, but any write threw ModificationOutsideTransactionException — the
// executor's ambient Transaction covers only the ACTIVE document, and
// ScriptApiDenylist check 1 unconditionally refuses a script opening its own.
// The fix does NOT narrow that check (a runtime document-identity comparison
// was assessed and rejected: Revit hands back different wrapper objects for
// "the same" document depending on the API entry point). Instead the connector
// opens and owns a Transaction/TransactionGroup for each document a script
// creates, in the same step that creates it — the new
// CreateProjectDocument/CreateFamilyDocument script globals.
//
// So there are now TWO creation paths and they differ in exactly one way:
// the raw Application members still work and are still READ-ONLY; the new
// globals give a writable document. TestApplicationCreatesDocuments'
// NewDocumentIsOutsideTheAmbientTransaction subtest pins the read-only half and
// must keep passing alongside this — the raw path was not broken or replaced.
//
// SESSION COST, as with TestApplicationCreatesDocuments: every run leaves its
// documents open in the live session. Same accepted trade, same reason.
func TestCreatedDocumentIsWritable(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	// Two subtests below COUNT levels at a given elevation across every open
	// document, and this case deliberately leaves its documents open (nothing
	// closes them -- see the note above). A hardcoded elevation therefore makes
	// them pass once and fail on the second run in the same Revit session, with
	// a count of 2 -- observed live, and it reads exactly like a double-commit
	// bug rather than a stale fixture. Unique-per-run elevations keep "exactly
	// one" a real assertion instead of a run-order artifact.
	base := 1000 + float64(time.Now().UnixNano()/1e6%500000)/100.0
	elevA := strconv.FormatFloat(base+0.001, 'f', 4, 64)
	elevB := strconv.FormatFloat(base+0.002, 'f', 4, 64)
	elevAmbient := strconv.FormatFloat(base+0.003, 'f', 4, 64)
	elevRolledBack := strconv.FormatFloat(base+0.004, 'f', 4, 64)

	// The headline: create, write, and read the write back, all in one script.
	// Level.Create is the same write TestCreateLevel makes against the ambient
	// document, chosen so the only variable here is WHICH document it lands in.
	t.Run("CreateProjectDocumentThenWriteToIt", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, `
var doc = CreateProjectDocument();
var before = new Autodesk.Revit.DB.FilteredElementCollector(doc)
    .OfClass(typeof(Autodesk.Revit.DB.Level)).GetElementCount();
var level = Autodesk.Revit.DB.Level.Create(doc, 4242.0);
var after = new Autodesk.Revit.DB.FilteredElementCollector(doc)
    .OfClass(typeof(Autodesk.Revit.DB.Level)).GetElementCount();
return new {
  docType = doc.GetType().FullName,
  isTheAmbientDocument = object.ReferenceEquals(doc, Document),
  created = after == before + 1,
  levelId = level.Id.Value
};
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
		}
		for _, want := range []string{
			"docType = Autodesk.Revit.DB.Document",
			// Guards against the whole test passing for the wrong reason: if
			// CreateProjectDocument ever returned the ambient document, every
			// other assertion here would still hold and nothing would be proven.
			"isTheAmbientDocument = False",
			"created = True",
		} {
			if !strings.Contains(out.Output, want) {
				t.Errorf("wanted %q in output: %s", want, out.Output)
			}
		}
	})

	// THE ASSERTION THAT ACTUALLY PROVES THE COMMIT. Writing inside one script
	// only shows the write was permitted; it says nothing about whether the
	// connector committed the created document's transaction after the script
	// returned. A SECOND execute_script call, finding the document again in
	// Application.Documents and reading the level back, is what distinguishes
	// "committed" from "written and then silently rolled back".
	t.Run("WritesToACreatedDocumentSurviveTheScript", func(t *testing.T) {
		// A distinctive elevation so the follow-up query cannot match a level
		// from the template or from another subtest's document.
		created := runScript(t, c, instanceID, documentID, `
var doc = CreateProjectDocument();
Autodesk.Revit.DB.Level.Create(doc, 4343.0);
return doc.Title;
`)
		if created.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", created.Status, created.Output)
		}
		title := strings.TrimSpace(created.Output)
		if title == "" {
			t.Fatalf("created document reported no title; output: %s", created.Output)
		}

		found := runScript(t, c, instanceID, documentID, `
int matches = 0;
foreach (Autodesk.Revit.DB.Document d in UIApplication.Application.Documents) {
  if (d.Title != `+strconv.Quote(title)+`) { continue; }
  foreach (Autodesk.Revit.DB.Level lv in new Autodesk.Revit.DB.FilteredElementCollector(d)
      .OfClass(typeof(Autodesk.Revit.DB.Level))) {
    if (System.Math.Abs(lv.Elevation - 4343.0) < 0.001) { matches++; }
  }
}
return "matches = " + matches + ";";
`)
		if found.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", found.Status, found.Output)
		}
		if !strings.Contains(found.Output, "matches = 1;") {
			t.Fatalf("the level written to a created document did not survive the script that wrote it -- the connector's managed transaction for that document did not commit; output: %s", found.Output)
		}
	})

	// A failing script must roll back EVERY document, not just the ambient one.
	// Written and then thrown away in one script, then checked from a second:
	// the created document still exists (nothing closes it) but must carry no
	// level at this elevation.
	t.Run("AThrowingScriptRollsBackCreatedDocumentsToo", func(t *testing.T) {
		// A run that writes to a created document and then throws. Read via
		// runRejectedScript, not runScript: a script that throws comes back as an MCP
		// tool error carrying the PRD §01 record, which decodeToolResult fatals on by
		// design. The title is printed to stdout BEFORE the throw, because the return
		// value dies with the script and the follow-up check needs to look at exactly
		// one document.
		thrown := runRejectedScript(t, c, instanceID, documentID, `
var doc = CreateProjectDocument();
Autodesk.Revit.DB.Level.Create(doc, `+elevRolledBack+`);
System.Console.WriteLine(doc.Title);
throw new System.InvalidOperationException("deliberate");
`)
		if thrown.Error.Code != "script-execution-failed" {
			t.Fatalf("expected code script-execution-failed, got %q (text: %s)", thrown.Error.Code, thrown.Text)
		}
		rolledBackTitle := strings.TrimSpace(thrown.Output)
		if rolledBackTitle == "" {
			t.Fatalf("the throwing run reported no stdout, so there is no document to check; record: %s", thrown.Text)
		}

		// That document still exists (nothing closes it) but must carry no level at
		// this elevation: its managed transaction was rolled back with everything else.
		check := runScript(t, c, instanceID, documentID, `
int matches = 0;
foreach (Autodesk.Revit.DB.Document d in UIApplication.Application.Documents) {
  if (d.Title != `+strconv.Quote(rolledBackTitle)+`) { continue; }
  foreach (Autodesk.Revit.DB.Level lv in new Autodesk.Revit.DB.FilteredElementCollector(d)
      .OfClass(typeof(Autodesk.Revit.DB.Level))) {
    if (System.Math.Abs(lv.Elevation - `+elevRolledBack+`) < 0.0001) { matches++; }
  }
}
return "matches = " + matches + ";";
`)
		if check.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", check.Status, check.Output)
		}
		if !strings.Contains(check.Output, "matches = 0;") {
			t.Fatalf("a throwing script left its write behind in a created document -- rollback does not cover every managed document; output: %s", check.Output)
		}
	})

	// The family-document counterpart. FamilyManager.NewType is a real write and
	// the API Phase D of the corpus plan actually goes through, so this is the
	// same shape of proof as the project case rather than a weaker one.
	t.Run("CreateFamilyDocumentThenWriteToIt", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, `
var app = UIApplication.Application;
string template = "";
if (System.IO.Directory.Exists(app.FamilyTemplatePath)) {
  foreach (var f in System.IO.Directory.EnumerateFiles(app.FamilyTemplatePath, "Generic Model.rft", System.IO.SearchOption.AllDirectories)) {
    template = f;
    break;
  }
}
if (template.Length == 0) { return "no-template"; }
var doc = CreateFamilyDocument(template);
var before = doc.FamilyManager.Types.Size;
doc.FamilyManager.NewType("MCPBridgeIssue24Type");
return new {
  isFamily = doc.IsFamilyDocument,
  typeAdded = doc.FamilyManager.Types.Size == before + 1
};
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
		}
		if strings.Contains(out.Output, "no-template") {
			t.Skip("no \"Generic Model.rft\" under Application.FamilyTemplatePath on this machine")
		}
		for _, want := range []string{"isFamily = True", "typeAdded = True"} {
			if !strings.Contains(out.Output, want) {
				t.Errorf("wanted %q in output: %s", want, out.Output)
			}
		}
	})

	// ScriptApiDenylist check 1 is UNCHANGED and stays unconditional -- that is
	// the property the whole approach was chosen to preserve, so it is asserted
	// rather than assumed. A script may not open its own Transaction even
	// against a document it created itself, because it no longer needs to.
	t.Run("ConstructingATransactionIsStillRefusedAgainstACreatedDocument", func(t *testing.T) {
		rejected := runRejectedScript(t, c, instanceID, documentID, `
var doc = CreateProjectDocument();
using (var tx = new Autodesk.Revit.DB.Transaction(doc, "mine")) { tx.Start(); tx.Commit(); }
return "opened";
`)
		if rejected.Error.Code != "script-api-denied" {
			t.Fatalf("expected code script-api-denied, got %q (text: %s)", rejected.Error.Code, rejected.Text)
		}
	})

	// And the mirror image: calling the new helper is NOT a denylist violation.
	// Covered at tier 1 too (TransactionScriptExecutorTests), but only live can
	// show it against the real, fully-bound Revit metadata Revit itself loads.
	t.Run("CallingTheCreationHelperIsNotADenylistViolation", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, `
var doc = CreateProjectDocument();
return "ok:" + (doc != null);
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
		}
		if !strings.Contains(out.Output, "ok:True") {
			t.Fatalf("unexpected output: %s", out.Output)
		}
	})

	// TWO created documents in one script, both written to, both committed.
	// This is the N-document case proper -- the single-created-document tests
	// above would all pass against an implementation that only ever tracked one.
	t.Run("TwoCreatedDocumentsBothCommit", func(t *testing.T) {
		created := runScript(t, c, instanceID, documentID, `
var a = CreateProjectDocument();
var b = CreateProjectDocument();
Autodesk.Revit.DB.Level.Create(a, `+elevA+`);
Autodesk.Revit.DB.Level.Create(b, `+elevB+`);
return a.Title + "|" + b.Title;
`)
		if created.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", created.Status, created.Output)
		}
		titles := strings.Split(strings.TrimSpace(created.Output), "|")
		if len(titles) != 2 {
			t.Fatalf("expected two document titles, got %q", created.Output)
		}

		// Scoped to the two documents by title rather than scanning every open one.
		// This case deliberately leaves documents open and the wider suite accumulates
		// dozens of them, so an all-documents scan grows until it blows the harness's
		// 20s tool timeout -- which then leaves the instance busy and fails every
		// later subtest for an unrelated reason. Observed live; keep queries scoped.
		check := runScript(t, c, instanceID, documentID, `
int a = 0, b = 0;
foreach (Autodesk.Revit.DB.Document d in UIApplication.Application.Documents) {
  bool isA = d.Title == `+strconv.Quote(titles[0])+`;
  bool isB = d.Title == `+strconv.Quote(titles[1])+`;
  if (!isA && !isB) { continue; }
  foreach (Autodesk.Revit.DB.Level lv in new Autodesk.Revit.DB.FilteredElementCollector(d)
      .OfClass(typeof(Autodesk.Revit.DB.Level))) {
    if (isA && System.Math.Abs(lv.Elevation - `+elevA+`) < 0.0001) { a++; }
    if (isB && System.Math.Abs(lv.Elevation - `+elevB+`) < 0.0001) { b++; }
  }
}
return "a = " + a + "; b = " + b + ";";
`)
		if check.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", check.Status, check.Output)
		}
		if !strings.Contains(check.Output, "a = 1;") || !strings.Contains(check.Output, "b = 1;") {
			t.Fatalf("both created documents were supposed to commit their own write; output: %s", check.Output)
		}
	})

	// LIVE FINDING, pinned because it is a real consequence of this change and a
	// surprising one: while the connector holds a managed transaction on a
	// document, REVIT ITSELF refuses to close it —
	// "Close is not allowed when there is any open sub-transaction, transaction
	// or transaction group." So a document made with CreateProjectDocument
	// cannot be closed from within the same script, even with
	// confirm_lifecycle_actions. This is exactly the rule the ambient document
	// has always been under; created documents have simply joined it.
	//
	// It also closes off the most obvious way a script could have made one
	// document's commit fail after another's had already succeeded — the
	// partial-commit case issue #24 flagged as undetermined. Revit refuses
	// before that state is reachable.
	//
	// If you genuinely want a throwaway document you can close, use the raw
	// Application.NewProjectDocument path — no transaction is opened for it, so
	// Close still works there (and it is read-only, which is the trade).
	t.Run("ACreatedDocumentCannotBeClosedWhileItsTransactionIsOpen", func(t *testing.T) {
		out := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID, `
var doc = CreateProjectDocument();
Autodesk.Revit.DB.Level.Create(doc, 5050.0);
try { doc.Close(false); return "closed"; }
catch (Autodesk.Revit.Exceptions.InvalidOperationException ex) { return "refused: " + ex.Message; }
`, map[string]any{"confirm_lifecycle_actions": true}))
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
		}
		if !strings.Contains(out.Output, "refused:") {
			t.Fatalf("closing a created document was not refused; if Revit now allows this, the partial-commit reasoning in ManagedDocumentTransactions needs revisiting; output: %s", out.Output)
		}
	})

	// The per-document Failures API is really wired for created documents, not
	// only for the ambient one: a warning raised inside a document the script
	// created is auto-dismissed and reported in notices[] (PRD §07). This is the
	// only route by which one document's commit can fail while another's
	// succeeds — an ERROR-level posting forces that document's rollback — so it
	// matters that the plumbing demonstrably reaches created documents at all.
	t.Run("FailuresInACreatedDocumentAreReportedAsNotices", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, `
var doc = CreateProjectDocument();
var lvl = Autodesk.Revit.DB.Level.Create(doc, 0.0);
doc.Create.NewRoom(lvl, new Autodesk.Revit.DB.UV(5, 5));
return "room-created";
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
		}
		var found bool
		for _, n := range out.Notices {
			if n.Code == "transaction-failure-warning" {
				found = true
			}
		}
		if !found {
			t.Fatalf("a Revit warning raised inside a CREATED document did not reach notices[] -- the per-document Failures API is not wired; notices: %+v", out.Notices)
		}
	})

	// The ambient document is still committed normally when a script also
	// creates documents -- the generalization must not have cost the original
	// single-document behaviour. Written last in commit order by design (see
	// ManagedDocumentTransactions), which is exactly why it needs its own check.
	t.Run("TheAmbientDocumentStillCommitsAlongsideCreatedOnes", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, `
var created = CreateProjectDocument();
Autodesk.Revit.DB.Level.Create(created, `+elevA+`);
Autodesk.Revit.DB.Level.Create(Document, `+elevAmbient+`);
return "done";
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
		}

		check := runScript(t, c, instanceID, documentID, `
int ambient = 0;
foreach (Autodesk.Revit.DB.Level lv in new Autodesk.Revit.DB.FilteredElementCollector(Document)
    .OfClass(typeof(Autodesk.Revit.DB.Level))) {
  if (System.Math.Abs(lv.Elevation - `+elevAmbient+`) < 0.0001) { ambient++; }
}
return "ambient = " + ambient + ";";
`)
		if check.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", check.Status, check.Output)
		}
		if !strings.Contains(check.Output, "ambient = 1;") {
			t.Fatalf("the ambient document's own write did not commit when the script also created a document; output: %s", check.Output)
		}
	})
}
