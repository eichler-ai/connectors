//go:build harness

package harness_test

import (
	"fmt"
	"strings"
	"testing"
	"time"
)

// TestDocumentIdRouting is the tier-2 pin for the v1 remediation series' two
// paired features: execute_script's document_id genuinely routing (the
// long-standing accepted-but-ignored parameter -- CONVENTIONS.md's
// advertised-but-unimplemented clause was written about it), and the live
// document-snapshot push that closes issue #30 (register was a one-shot
// connect-time snapshot before). They are one architectural unit: routing is
// only usable because list_instances' document ids are now live.
func TestDocumentIdRouting(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	t.Run("ExplicitActiveDocumentIdRoutes", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, "return Document.Title;")
		if out.Status != "success" || out.Output == "" {
			t.Fatalf("routing to the active document by its own id should just work: %+v", out)
		}
	})

	t.Run("UnknownDocumentIdFailsLoudlyWithCandidates", func(t *testing.T) {
		rej := runRejectedScript(t, c, instanceID, "doc-0000000000000000", "return 1;")
		if rej.Error.Code != "document-not-found" {
			t.Fatalf("code = %q, want document-not-found (never a silent active-document fallback)\nfull: %s", rej.Error.Code, rej.Text)
		}
		// The candidates list names what IS addressable -- the real open
		// document's id must be in the error's detail.
		if !strings.Contains(rej.Text, documentID) {
			t.Errorf("the error should carry an open_documents candidates list naming %s\nfull: %s", documentID, rej.Text)
		}
		if len(rej.Error.Remedy) == 0 {
			t.Errorf("PRD §01: this error has a mechanical next step and must carry a remedy\nfull: %s", rej.Text)
		}
	})

	t.Run("BackgroundDocumentRoutingAndLiveSnapshotPush", func(t *testing.T) {
		// Open a SECOND document in the background (no UI activation) by
		// copying the active one's file -- Application.OpenDocumentFile does
		// not change the active document, so the copy stays background.
		copyTitle := fmt.Sprintf("routing-copy-%d", time.Now().UnixNano())
		openScript := fmt.Sprintf(`
var source = Document.PathName;
var dest = System.IO.Path.Combine(System.IO.Path.GetTempPath(), %q + ".rvt");
System.IO.File.Copy(source, dest, true);
UIApplication.Application.OpenDocumentFile(dest);
return dest;`, copyTitle)
		opened := runScript(t, c, instanceID, documentID, openScript)
		if opened.Status != "success" {
			t.Fatalf("opening the background copy failed: %+v", opened)
		}
		copyPath := strings.TrimSpace(opened.Output)

		// Best-effort cleanup regardless of what happens below, via the shared
		// helper: close the background copy FROM the active document's context
		// (a routed document's own ambient transaction makes it un-closable
		// from within itself -- PRD §14's investigated behavior), then delete
		// the on-disk copy.
		t.Cleanup(func() { closeDocumentByTitle(t, c, instanceID, documentID, copyTitle, copyPath) })

		// THE ISSUE #30 ASSERTION: the new document must appear in
		// list_instances WITHOUT any reconnect or broker restart -- the
		// add-in pushes a fresh register on DocumentOpened. Poll briefly
		// (the push happens within moments of the open completing).
		var copyDocumentID string
		deadline := time.Now().Add(20 * time.Second)
		for time.Now().Before(deadline) {
			raw, err := c.CallTool("list_instances", map[string]any{}, 10*time.Second)
			if err != nil {
				t.Fatalf("list_instances: %v", err)
			}
			instances := decodeToolResult[listInstancesOut](t, raw)
			for _, inst := range instances.Instances {
				if inst.InstanceID != instanceID {
					continue
				}
				for _, doc := range inst.Documents {
					if doc.Title == copyTitle {
						copyDocumentID = doc.DocumentID
					}
				}
			}
			if copyDocumentID != "" {
				break
			}
			time.Sleep(500 * time.Millisecond)
		}
		if copyDocumentID == "" {
			t.Fatalf("the background document %q never appeared in list_instances -- the live snapshot push (issue #30) did not happen", copyTitle)
		}

		// THE ROUTING ASSERTION: a script addressed at the background
		// document runs against IT (Document.Title is the copy's), and
		// UIDocument is null there -- Revit has no UIDocument for a
		// background document, and handing over the active one's would be
		// the exact wrong-document hazard routing exists to end.
		routed := runScript(t, c, instanceID, copyDocumentID,
			`return (UIDocument == null ? "uidoc-null:" : "uidoc-nonnull:") + Document.Title;`)
		if routed.Output != "uidoc-null:"+copyTitle {
			t.Fatalf("routed run output = %q, want %q -- the script must run against the addressed background document with a null UIDocument", routed.Output, "uidoc-null:"+copyTitle)
		}
	})
}
