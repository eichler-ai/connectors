//go:build harness

package harness_test

import (
	"encoding/json"
	"fmt"
	"strconv"
	"strings"
	"testing"

	"github.com/eichler-ai/connectors/revit/test-harness/mcpclient"
)

// createBlankFixtureDocument creates one blank, writable project document via
// the CreateProjectDocument script global (default template; issue #24) and
// returns its Title -- the ONLY way a later execute_script call can find it
// again. There is no document_id for a created document: it never appears in
// list_instances (a one-shot snapshot taken at connect, PRD §05), and
// execute_script always routes by ActiveUIDocument, never by document_id
// (see TestApplicationCreatesDocuments/CreatedDocumentStaysInApplicationDocuments
// and the coverage-plan's own "document_id resolution" note). Every later
// script that wants this document must find it by Title in
// UIApplication.Application.Documents -- see fixtureLookupPreamble below.
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
	out := runScript(t, c, instanceID, documentID, `return CreateProjectDocument().Title;`)
	if out.Status != "success" {
		t.Fatalf("failed to create blank fixture document: status=%q output=%s", out.Status, out.Output)
	}
	title := strings.TrimSpace(out.Output)
	if title == "" {
		t.Fatalf("blank fixture document reported an empty title; output=%q", out.Output)
	}
	t.Cleanup(func() { closeFixtureDocument(t, c, instanceID, documentID, title) })
	return title
}

// closeFixtureDocument closes the fixture document named by title via
// Document.Close(false) -- the explicit-bool overload, never the no-arg one,
// so there is no "save changes?" prompt to hang behind in this headless
// script-execution model (these documents are never saved to begin with;
// nothing is lost by discarding). Close is confirm_lifecycle_actions-gated
// (PRD §14) since it acts outside the ambient transaction; that's correct
// here too, not worked around -- see callExecuteScriptWith's extra param.
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
func closeFixtureDocument(t *testing.T, c *mcpclient.Client, instanceID, documentID, title string) {
	t.Helper()
	script := fixtureLookupPreamble(title) + `
doc.Close(false);
return "closed";
`
	raw := callExecuteScriptWith(t, c, instanceID, documentID, script, map[string]any{"confirm_lifecycle_actions": true})

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
		t.Logf("cleanup: failed to close fixture document %q: status=%q output=%s", title, out.Status, out.Output)
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
OpenForWriting(doc);
`, strconv.Quote(title))
}
