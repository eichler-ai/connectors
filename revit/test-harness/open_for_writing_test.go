//go:build harness

package harness_test

import (
	"fmt"
	"strings"
	"testing"
)

// TestOpenForWritingSafety exercises OpenForWriting's actual safety guarantees against a real Revit
// instance -- independent PR review (2nd round) finding: nothing anywhere pinned these as regression
// tests. The PR body claimed the two negative paths (adopt-the-ambient, double-adopt) were "live-verified"
// via one-off mcp__revit__execute_script calls before being committed, and the rollback-on-throw case --
// OpenForWriting's headline guarantee -- had no coverage of any kind, at any tier: ManagedDocumentTransactions
// itself is only tier-1-testable for the generic Open(IDocumentAdapter, DocumentOrigin) guard logic, never
// for what a real Document actually does when OpenForWriting-adopted then rolled back. Hand-verification is
// not a regression test; this project's own standing rule is that these live cases get encoded once found.
func TestOpenForWritingSafety(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	// RollsBackOnThrow: OpenForWriting's actual safety guarantee -- a script that adopts a document,
	// writes to it, then throws must leave NOTHING behind, exactly like a document the script created
	// this run. Creates a Level at a distinctive elevation, throws, then a SEPARATE follow-up
	// execute_script call queries for that elevation and asserts nothing landed -- the same
	// "committed vs did not commit" proof CreatedThisRun rollback already gets in
	// TestCreatedDocumentIsWritable/AThrowingScriptRollsBackCreatedDocumentsToo, now extended to
	// AdoptedExisting.
	t.Run("RollsBackOnThrow", func(t *testing.T) {
		const distinctiveElevation = "777.0"

		_ = runRejectedScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+fmt.Sprintf(`
Autodesk.Revit.DB.Level.Create(doc, %s);
throw new System.Exception("deliberate failure after Connector.OpenForWriting write, to prove rollback");
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
			t.Fatalf("follow-up check failed: status=%q return_value=%s", check.Status, check.ReturnValue)
		}
		if !strings.Contains(check.ReturnValue, "\"survived\":false") {
			t.Errorf("expected the Level created before the throw to have been rolled back; return_value: %s", check.ReturnValue)
		}
	})

	// OnTheAmbientDocument_FailsCleanly: OpenForWriting(Document) where Document is the SAME document
	// this run's ambient transaction already covers -- the double-open guard's primary documented
	// trigger. Must be refused with the signposted InvalidOperationException, not a raw Revit exception
	// from a second Transaction.Start().
	t.Run("OnTheAmbientDocument_FailsCleanly", func(t *testing.T) {
		rejection := runRejectedScript(t, c, instanceID, documentID, `
Connector.OpenForWriting(Document);
return "unreachable";
`)
		if !strings.Contains(rejection.Error.Message, "already open") {
			t.Errorf("expected the double-open guard's message, got: %s", rejection.Error.Message)
		}
	})

	// CalledTwiceOnTheSameDocument_FailsCleanly: the other trigger for the same guard -- adopting a
	// document this run already adopted earlier via OpenForWriting.
	t.Run("CalledTwiceOnTheSameDocument_FailsCleanly", func(t *testing.T) {
		rejection := runRejectedScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
Connector.OpenForWriting(doc);
return "unreachable";
`)
		if !strings.Contains(rejection.Error.Message, "already open") {
			t.Errorf("expected the double-open guard's message, got: %s", rejection.Error.Message)
		}
	})
}
