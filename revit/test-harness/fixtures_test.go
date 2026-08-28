//go:build harness

package harness_test

import (
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
	return title
}

// fixtureLookupPreamble returns a C# statement block that finds the document
// with the given title (from a prior createBlankFixtureDocument call) and
// assigns it to a local variable named `doc`, throwing a clear error if it
// can't be found. Every Phase-bundle subtest that targets the shared fixture
// document should start its script with this, since a created document has
// no document_id and must be re-found by Title in every separate
// execute_script call (see createBlankFixtureDocument above).
func fixtureLookupPreamble(title string) string {
	return fmt.Sprintf(`
Autodesk.Revit.DB.Document doc = null;
foreach (Autodesk.Revit.DB.Document candidate in UIApplication.Application.Documents) {
  if (candidate.Title == %s) { doc = candidate; break; }
}
if (doc == null) { throw new System.Exception("fixture document not found by title: " + %s); }
`, strconv.Quote(title), strconv.Quote(title))
}
