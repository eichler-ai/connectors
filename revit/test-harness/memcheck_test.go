//go:build harness

package harness_test

import (
	"fmt"
	"strings"
	"testing"
)

// TestOpenForWritingMemoryCycles is a throwaway diagnostic, not part of the
// coverage corpus: N true cross-call cycles (create in one execute_script
// call, OpenForWriting+write in a separate one, close in a third) -- the
// exact pattern the OpenForWriting feature and its memory-safety analysis
// are about. Run with Revit's process memory sampled before and after via
// `prlctl exec ... Get-Process` externally; this test only drives the
// cycles themselves.
func TestOpenForWritingMemoryCycles(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	const cycles = 6

	for i := 0; i < cycles; i++ {
		created := runScript(t, c, instanceID, documentID, `return CreateProjectDocument().Title;`)
		if created.Status != "success" {
			t.Fatalf("cycle %d: create failed: status=%q output=%s", i, created.Status, created.Output)
		}
		title := strings.TrimSpace(created.Output)

		written := runScript(t, c, instanceID, documentID, fixtureWritePreamble(title)+
			fmt.Sprintf("var level = Autodesk.Revit.DB.Level.Create(doc, %d.0);\nreturn level != null;\n", 10+i))
		if written.Status != "success" {
			t.Fatalf("cycle %d: write failed: status=%q output=%s", i, written.Status, written.Output)
		}

		closeFixtureDocument(t, c, instanceID, documentID, title)
	}
}

// TestOpenDocumentCount reports how many documents Application.Documents
// currently holds -- diagnostic, to distinguish "documents that should have
// been closed are still open" from "memory grew despite documents actually
// being closed" while investigating the memory-cycle numbers.
func TestOpenDocumentCount(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	out := runScript(t, c, instanceID, documentID, `
var titles = new System.Collections.Generic.List<string>();
foreach (Autodesk.Revit.DB.Document d in UIApplication.Application.Documents) { titles.Add(d.Title); }
return string.Join(", ", titles) + " (count=" + titles.Count + ")";
`)
	if out.Status != "success" {
		t.Fatalf("status=%q output=%s", out.Status, out.Output)
	}
	t.Logf("open documents: %s", out.Output)
}
