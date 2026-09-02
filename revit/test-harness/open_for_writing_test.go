//go:build harness

package harness_test

import (
	"fmt"
	"strings"
	"testing"
)

// TestWithTransactionAdoptsAnExistingDocument exercises the route that replaced Connector.OpenForWriting
// in #146 Phase 3: a document a PRIOR call created and left open, reached through Application.Documents
// by Title, is written to inside a Connector.WithTransaction block. The connector opens a group for it on
// first touch (adopting it for the rest of the run) and the block's transaction inside that -- so the
// headline safety guarantee is the same as for any managed document: a script that writes and then
// throws leaves NOTHING behind. Live, because what a real Document does when adopted then rolled back is
// not something ManagedDocumentTransactions' fakes can show.
func TestWithTransactionAdoptsAnExistingDocument(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	t.Run("RollsBackOnThrow", func(t *testing.T) {
		const distinctiveElevation = "777.0"

		_ = runRejectedScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+fmt.Sprintf(`
Connector.WithTransaction(doc, () => { Autodesk.Revit.DB.Level.Create(doc, %s); });
throw new System.Exception("deliberate failure after the adopted document's block committed, to prove the group rolls it back");
`, distinctiveElevation))

		check := runScript(t, c, instanceID, documentID, fixtureLookupPreamble(fixtureTitle)+fmt.Sprintf(`
var collector = new Autodesk.Revit.DB.FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Level));
var survived = 0;
foreach (Autodesk.Revit.DB.Level lv in collector) {
  if (System.Math.Abs(lv.Elevation - %s) < 0.001) { survived++; }
}
return new { survived = survived > 0 };
`, distinctiveElevation))
		if check.Status != "success" {
			t.Fatalf("follow-up check failed: status=%q %s", check.Status, check.diag())
		}
		if !strings.Contains(check.ReturnValue, "\"survived\":false") {
			t.Errorf("expected the Level committed by the block before the throw to have been rolled back with the adopted document's group; %s", check.diag())
		}
	})

	// NestedOnTheSameDocument_IsRefused: the one thing a block will not do is nest on the same document
	// -- the guard that used to fire for OpenForWriting-on-the-ambient-document lives here now.
	t.Run("NestedOnTheSameDocument_IsRefused", func(t *testing.T) {
		rejection := runRejectedScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
Connector.WithTransaction(doc, () => {
  Connector.WithTransaction(doc, () => { });
});
return "unreachable";
`)
		if !strings.Contains(rejection.Error.Message, "cannot be nested on the same document") {
			t.Errorf("expected the nesting guard's message, got: %s", rejection.Error.Message)
		}
	})

	// TwoDocumentsInOneRun: adopting a second document beside the routed one, each with its own group,
	// and a throw undoes BOTH -- the N-documents guarantee ManagedDocumentTransactions' class comment
	// makes, live.
	t.Run("AThrowRollsBackEveryAdoptedDocument", func(t *testing.T) {
		_ = runRejectedScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
Connector.WithTransaction(doc, () => { Autodesk.Revit.DB.Level.Create(doc, 778.0); });
Connector.WithTransaction(Document, () => { Autodesk.Revit.DB.Level.Create(Document, 779.0); });
throw new System.Exception("deliberate: both documents' groups must roll back");
`)
		check := runScript(t, c, instanceID, documentID, fixtureLookupPreamble(fixtureTitle)+`
int fixture = 0, active = 0;
foreach (Autodesk.Revit.DB.Level lv in new Autodesk.Revit.DB.FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Level))) { if (System.Math.Abs(lv.Elevation - 778.0) < 0.001) fixture++; }
foreach (Autodesk.Revit.DB.Level lv in new Autodesk.Revit.DB.FilteredElementCollector(Document).OfClass(typeof(Autodesk.Revit.DB.Level))) { if (System.Math.Abs(lv.Elevation - 779.0) < 0.001) active++; }
return new { fixture, active };
`)
		if check.Status != "success" {
			t.Fatalf("follow-up check failed: status=%q %s", check.Status, check.diag())
		}
		for _, want := range []string{`"fixture":0`, `"active":0`} {
			if !strings.Contains(check.ReturnValue, want) {
				t.Errorf("wanted %s -- a thrown script must roll back every document it wrote to, adopted or routed; %s", want, check.diag())
			}
		}
	})
}
