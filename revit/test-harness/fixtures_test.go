//go:build harness

package harness_test

import (
	"encoding/json"
	"fmt"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/revit/test-harness/mcpclient"
)

// createBlankFixtureDocument creates one blank, writable project document via
// the CreateProjectDocument script global (default template; issue #24) and
// returns its Title. (Since the v1 remediation series, a created document DOES
// get a routable tmp- document_id in list_instances -- live snapshot push plus
// title-derived identity -- but these helpers predate that and in-script
// lookup by Title in UIApplication.Application.Documents remains simple,
// correct, and free of a list_instances round trip, so they keep using it.
// See fixtureLookupPreamble below.)
//
// Call this ONCE per bundle (per t.Run group), not once per subtest: creating
// a document has real cost, and the coverage plan's fixture-system section is
// explicit that blank-per-bundle, not blank-per-check, is the model to use.
//
// Registers a t.Cleanup that closes this document when the bundle's test
// function returns (LIFO -- runs once, after every subtest has finished, not
// per-subtest). Confirmed live: a single Phase A session with no cleanup at
// all accumulated enough open, never-disposed documents to trigger Revit's
// own "Virtual Memory - High Usage" warning within one working session --
// the coverage plan's original "restart Revit periodically during a full
// corpus run" mitigation is nowhere near proactive enough once there are
// dozens of bundles, each leaving a document open forever. Cleanup failing
// is logged, not fatal -- a bundle's own test results must never be
// swallowed by a teardown problem.
func createBlankFixtureDocument(t *testing.T, c *mcpclient.Client, instanceID, documentID string) string {
	t.Helper()
	out := runScript(t, c, instanceID, documentID, `return Connector.CreateProjectDocument().Title;`)
	if out.Status != "success" {
		t.Fatalf("failed to create blank fixture document: status=%q return_value=%s", out.Status, out.ReturnValue)
	}
	title := strings.TrimSpace(out.ReturnValue)
	if title == "" {
		t.Fatalf("blank fixture document reported an empty title; return_value=%q", out.ReturnValue)
	}
	t.Cleanup(func() { closeDocumentByTitle(t, c, instanceID, documentID, title, "") })
	return title
}

// closeDocumentByTitle closes the open document named by title via
// Document.Close(false) -- the explicit-bool overload, never the no-arg one,
// so there is no "save changes?" prompt to hang behind in this headless
// script-execution model (harness-created documents are never saved; nothing
// is lost by discarding). Close is confirm_lifecycle_actions-gated (PRD §14)
// since it acts outside the ambient transaction; that's correct here too, not
// worked around -- see callExecuteScriptWith's extra param. The closing script
// runs routed to the ACTIVE document (documentID), which is what makes closing
// a created document possible at all: a created document holds its managed
// transaction only during the run that created it, so a later run with no
// transaction on it may close it (PRD §14's live-pinned rule -- see
// ACreatedDocumentCannotBeClosedWhileItsTransactionIsOpen for the
// within-the-same-run refusal).
//
// deletePathAfter, when non-empty, is a file to best-effort File.Delete after
// the close -- for cases (document_routing_test.go) whose fixture is an
// on-disk copy rather than an unsaved document. Same script, so one round
// trip covers both.
//
// This is the shared cleanup helper every document-creating case registers
// via t.Cleanup (or calls directly). The suite used to leave 8-12 unsaved
// documents open per full run -- deliberately at first ("restart Revit
// between corpus runs"), but the live snapshot push (issue #30) made every
// leftover visible in list_instances, where it polluted later cases'
// targetDocument choice and, before targetDocument learned to prefer the
// active document, broke them outright. Cleanup is cheap now that closing is
// one confirm-gated script; the restart-Revit posture is gone.
//
// Independent PR review finding: this used to route through decodeToolResult,
// which calls t.Fatalf on an isError MCP response. Cleanup running in
// t.Cleanup happens after every subtest in the bundle has already recorded
// its own pass/fail -- a t.Fatalf here doesn't change that outcome, it just
// panics the cleanup goroutine and can mask which subtest, if any, actually
// caused a lingering document. A cleanup failure is realistic (the fixture
// document may already be in an odd state from whatever a subtest did to it)
// and must be logged, never fatal, exactly like the pre-existing
// out.Status != "success" branch below already treats a "success"-shaped
// failure. Decodes the envelope by hand here instead of reusing
// decodeToolResult specifically to avoid its Fatalf-on-isError behavior.
func closeDocumentByTitle(t *testing.T, c *mcpclient.Client, instanceID, documentID, title, deletePathAfter string) {
	t.Helper()
	deleteStatement := ""
	if deletePathAfter != "" {
		deleteStatement = "try { System.IO.File.Delete(" + strconv.Quote(deletePathAfter) + "); } catch {}\n"
	}
	// The lookup-and-close is wrapped so the file deletion runs UNCONDITIONALLY
	// (independent PR review finding): a document already closed (or never
	// created) must not leave the on-disk copy behind just because the by-Title
	// lookup threw. The close failure is still reported, via the return value.
	script := `
string closeError = "";
try {
` + fixtureLookupPreamble(title) + `
doc.Close(false);
} catch (System.Exception ex) { closeError = ex.Message; }
` + deleteStatement + `return closeError.Length == 0 ? "closed" : "close-failed: " + closeError;
`
	// Transport errors are logged, not fatal -- this helper runs inside
	// t.Cleanup, where its own never-fatal contract (see the doc comment) has
	// to cover the CallTool layer too, not just the decode below (independent
	// PR review finding: callExecuteScriptWith's Fatalf broke that promise).
	raw, err := c.CallTool("execute_script", map[string]any{
		"instance_id":               instanceID,
		"document_id":               documentID,
		"script":                    script,
		"confirm_lifecycle_actions": true,
	}, 45*time.Second)
	if err != nil {
		t.Logf("cleanup: close-document call for %q failed at the transport layer: %v", title, err)
		return
	}

	var tr toolResult
	if err := json.Unmarshal(raw, &tr); err != nil {
		t.Logf("cleanup: failed to decode close-fixture-document response for %q: %v\nraw: %s", title, err, raw)
		return
	}
	if tr.IsError {
		text := "(no content)"
		if len(tr.Content) > 0 {
			text = tr.Content[0].Text
		}
		t.Logf("cleanup: closing fixture document %q returned an error: %s", title, text)
		return
	}

	var out executeScriptOut
	if err := json.Unmarshal(tr.StructuredContent, &out); err != nil {
		t.Logf("cleanup: failed to decode close-fixture-document structuredContent for %q: %v\nraw: %s", title, err, tr.StructuredContent)
		return
	}
	if out.Status != "success" {
		t.Logf("cleanup: failed to close fixture document %q: status=%q return_value=%s", title, out.Status, out.ReturnValue)
		return
	}
	if strings.Contains(out.ReturnValue, "close-failed:") {
		t.Logf("cleanup: closing document %q reported: %s", title, strings.TrimSpace(out.ReturnValue))
	}
}

// fixtureLookupPreamble returns a C# statement block that finds the document
// with the given title (from a prior createBlankFixtureDocument call) and
// assigns it to a local variable named `doc`, throwing a clear error if it
// can't be found. Every Phase-bundle subtest that targets the shared fixture
// document should start its script with this, since a created document has
// no document_id and must be re-found by Title in every separate
// execute_script call (see createBlankFixtureDocument above).
//
// Independent PR review finding: this used to take the FIRST Title match and
// stop, silently tolerating a same-titled second document (e.g. a stale one
// left over from a prior failed cleanup, or a coincidental Title collision
// with something else open in the session) by picking whichever one Revit
// happened to enumerate first -- a subtest could then silently read or write
// the wrong document instead of failing loudly. Now collects every match and
// throws unless there is exactly one.
func fixtureLookupPreamble(title string) string {
	return fmt.Sprintf(`
Autodesk.Revit.DB.Document doc = null;
int matchCount = 0;
foreach (Autodesk.Revit.DB.Document candidate in UIApplication.Application.Documents) {
  if (candidate.Title == %s) { doc = candidate; matchCount++; }
}
if (matchCount == 0) { throw new System.Exception("fixture document not found by title: " + %s); }
if (matchCount > 1) { throw new System.Exception("fixture document title is ambiguous -- " + matchCount + " open documents share the title: " + %s); }
`, strconv.Quote(title), strconv.Quote(title), strconv.Quote(title))
}

// fixtureWritePreamble is fixtureLookupPreamble plus an unsaved-document assertion plus
// OpenForWriting(doc) -- use this instead of fixtureLookupPreamble in any subtest that WRITES to
// the fixture document. Without OpenForWriting, `doc` is fully readable but every write throws
// "Attempt to modify the model outside of transaction": createBlankFixtureDocument's own
// CreateProjectDocument call opened a managed transaction that already committed and closed the
// moment THAT script returned, so a later, separate execute_script call finds an ordinary,
// un-transacted document -- confirmed live, the bug that motivated adding the OpenForWriting
// script global in the first place. Kept as a distinct helper from fixtureLookupPreamble, not
// folded into it unconditionally, so a read-only subtest's own script signals its intent and
// never pays for (or risks) a write transaction it doesn't need.
//
// Independent PR review finding: the PathName assertion below is what actually keeps this helper
// scoped to what it's meant for -- a throwaway document createBlankFixtureDocument itself made
// this session. Without it, a Title collision with a real, saved, on-disk document (unlikely, but
// exactly the kind of unlikely a fixture helper should not depend on excluding by luck) would
// pass fixtureLookupPreamble's now-uniqueness-checked match and then OpenForWriting would happily
// adopt and this subtest would write into it. PathName is empty for a document that has never
// been saved, so asserting it here is a direct, load-bearing check that this is that
// document, not a defensive nicety.
func fixtureWritePreamble(title string) string {
	return fixtureLookupPreamble(title) + fmt.Sprintf(`
if (!string.IsNullOrEmpty(doc.PathName)) { throw new System.Exception("fixture document " + %s + " is unexpectedly saved to disk (PathName=" + doc.PathName + "); refusing to write to what may not be the throwaway fixture document"); }
Connector.OpenForWriting(doc);
`, strconv.Quote(title))
}

// cleanupTitles extracts every "cleanup-title=<Title>;" marker a script
// printed to stdout. Document-creating scripts whose RETURN value is already
// spoken for (an anonymous result object, a status string an assertion
// matches on) print this marker instead --
// System.Console.WriteLine("cleanup-title=" + doc.Title + ";") -- so the Go
// side can register closeDocumentByTitle without changing what the test
// asserts. Since issue #117 split the wire fields, stdout is Output and the
// script's returned value is ReturnValue, so the marker and the assertion no
// longer share a field at all — this reads Output, and every assertion in the
// suite reads ReturnValue. The trailing semicolon terminates the title the same way the
// suite's counted assertions terminate numbers: without it, a title that is
// a prefix of another could not be extracted unambiguously.
func cleanupTitles(output string) []string {
	var titles []string
	rest := output
	for {
		idx := strings.Index(rest, "cleanup-title=")
		if idx < 0 {
			return titles
		}
		rest = rest[idx+len("cleanup-title="):]
		end := strings.Index(rest, ";")
		if end < 0 {
			return titles
		}
		if title := strings.TrimSpace(rest[:end]); title != "" {
			titles = append(titles, title)
		}
		rest = rest[end+1:]
	}
}

// registerCreatedDocumentCleanup registers a closeDocumentByTitle t.Cleanup
// for every cleanup-title marker in output, logging (not failing) when a
// script that was expected to report one didn't -- cleanup problems must
// never change a test's own verdict.
func registerCreatedDocumentCleanup(t *testing.T, c *mcpclient.Client, instanceID, documentID, output string) {
	t.Helper()
	titles := cleanupTitles(output)
	if len(titles) == 0 {
		t.Logf("cleanup: no cleanup-title marker in output; a created document may be left open: %s", output)
		return
	}
	for _, title := range titles {
		title := title
		t.Cleanup(func() { closeDocumentByTitle(t, c, instanceID, documentID, title, "") })
	}
}
