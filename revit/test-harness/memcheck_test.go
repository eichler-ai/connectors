//go:build harness

package harness_test

import (
	"fmt"
	"os"
	"strings"
	"testing"
)

// memcheckGate skips a diagnostic test unless MCP_HARNESS_MEMCHECK is set. Independent PR review
// finding: neither test in this file was actually gated despite this file's own doc comment (and
// the README's) claiming they are "not run as part of a normal test pass" -- an unfiltered
// `go test -tags harness ./...` executed both of them anyway, cycling real Revit documents every
// time, which contradicts their own stated purpose as opt-in, ready-made diagnostics for
// revisiting issue #31, not corpus regression tests.
func memcheckGate(t *testing.T) {
	t.Helper()
	if os.Getenv("MCP_HARNESS_MEMCHECK") == "" {
		t.Skip("skipping memcheck diagnostic: set MCP_HARNESS_MEMCHECK=1 to run it explicitly")
	}
}

// TestOpenForWritingMemoryCycles is a throwaway diagnostic, not part of the
// coverage corpus: N true cross-call cycles (create in one execute_script
// call, OpenForWriting+write in a separate one, close in a third) -- the
// exact pattern the OpenForWriting feature and its memory-safety analysis
// are about. Run with Revit's process memory sampled before and after via
// `prlctl exec ... Get-Process` externally; this test only drives the
// cycles themselves.
func TestOpenForWritingMemoryCycles(t *testing.T) {
	memcheckGate(t)
	c, instanceID, documentID := targetDocument(t)
	const cycles = 6

	for i := 0; i < cycles; i++ {
		func() {
			created := runScript(t, c, instanceID, documentID, `return Connector.CreateProjectDocument().Title;`)
			if created.Status != "success" {
				t.Fatalf("cycle %d: create failed: status=%q return_value=%s", i, created.Status, created.ReturnValue)
			}
			title := strings.TrimSpace(created.ReturnValue)
			// Independent PR review finding: a t.Fatalf on the write below used to skip
			// closeFixtureDocument entirely, leaking the just-created document -- in the one test whose
			// whole purpose is measuring document-cycle memory, a leaked-on-failure document would
			// corrupt every later sample in the same run. Deferred within this per-iteration closure
			// (not the outer loop, which would keep every cycle's document open until the whole test
			// returns, defeating the actual "close between cycles" pattern being measured) so it always
			// runs once this cycle's create succeeded, regardless of what happens to the write.
			defer closeDocumentByTitle(t, c, instanceID, documentID, title, "")

			written := runScript(t, c, instanceID, documentID, fixtureWritePreamble(title)+
				fmt.Sprintf("var level = Autodesk.Revit.DB.Level.Create(doc, %d.0);\nreturn level != null;\n", 10+i))
			if written.Status != "success" {
				t.Fatalf("cycle %d: write failed: status=%q return_value=%s", i, written.Status, written.ReturnValue)
			}
		}()
	}
}

// TestOpenDocumentCount reports how many documents Application.Documents
// currently holds -- diagnostic, to distinguish "documents that should have
// been closed are still open" from "memory grew despite documents actually
// being closed" while investigating the memory-cycle numbers.
func TestOpenDocumentCount(t *testing.T) {
	memcheckGate(t)
	c, instanceID, documentID := targetDocument(t)

	out := runScript(t, c, instanceID, documentID, `
var titles = new System.Collections.Generic.List<string>();
foreach (Autodesk.Revit.DB.Document d in UIApplication.Application.Documents) { titles.Add(d.Title); }
return string.Join(", ", titles) + " (count=" + titles.Count + ")";
`)
	if out.Status != "success" {
		t.Fatalf("status=%q return_value=%s", out.Status, out.ReturnValue)
	}
	t.Logf("open documents: %s", out.ReturnValue)
}
