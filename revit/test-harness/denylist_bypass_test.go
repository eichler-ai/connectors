//go:build harness

package harness_test

import (
	"strings"
	"testing"
)

// TestConnectorOwnTypesAreNotReachableFromAScript pins the fix for a real,
// live-verified denylist bypass found by an independent review of the issue #24
// PR.
//
// THE BYPASS, and why the denylist could never have caught it:
// RoslynScriptRunner.LoadableReferences() references every assembly loaded in
// the Revit AppDomain, which includes MCPBridge.Core and MCPBridge.RevitAdapter
// themselves. While the adapters were public, a script could write
//
//	var raw = UIApplication.Application.NewProjectDocument(...);
//	new MCPBridge.RevitAdapter.RevitDocumentAdapter(raw).CreateTransaction("mine").Start();
//
// and own a real Revit Transaction the connector never tracked. ScriptApiDenylist
// cannot see it: the `new Autodesk.Revit.DB.Transaction(...)` happens inside
// RevitDocumentAdapter, in OUR assembly, while the AST walk only ever examines
// the script's own compilation. Confirmed live against Revit 2027 before the
// fix -- the script created a document, opened its own transaction, wrote a
// Level and COMMITTED it, reported status "success".
//
// THE FIX IS STRUCTURAL, not another denylist entry: every one of these types is
// now `internal` to its assembly, so the script cannot name it and fails to
// COMPILE. That is why these cases assert a rejection rather than a runtime
// error -- a runtime error would mean the type was still nameable.
//
// Tier 2 by construction: `internal` is only meaningful against the real
// assemblies as Revit actually loads them, and LoadableReferences() only has
// anything to reference inside a live Revit process.
func TestConnectorOwnTypesAreNotReachableFromAScript(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	for _, tc := range []struct{ name, typeName, script string }{
		{
			// The confirmed vector: a transaction on a document the script made
			// itself, which Revit genuinely permits, so nothing else would have
			// stopped it.
			name:     "RevitDocumentAdapterOnACreatedDocument",
			typeName: "RevitDocumentAdapter",
			script: `
var app = UIApplication.Application;
var raw = app.NewProjectDocument(app.DefaultProjectTemplate);
var tx = new MCPBridge.RevitAdapter.RevitDocumentAdapter(raw).CreateTransaction("bypass");
tx.Start();
return "opened an unmanaged transaction";`,
		},
		{
			// The same type against the ambient document. Before the fix this was
			// stopped only by Revit's own one-transaction-per-document rule --
			// incidental, not a guard the connector was choosing to apply.
			name:     "RevitDocumentAdapterOnTheAmbientDocument",
			typeName: "RevitDocumentAdapter",
			script:   `return new MCPBridge.RevitAdapter.RevitDocumentAdapter(Document).CreateTransaction("bypass") != null;`,
		},
		{
			// Takes the UIApplication a script already holds as a global, and
			// hands back adapters whose CreateTransaction is the vector above.
			name:     "RevitUiApplicationAdapter",
			typeName: "RevitUiApplicationAdapter",
			script:   `return new MCPBridge.RevitAdapter.RevitUiApplicationAdapter(UIApplication).CreateProjectDocument(null) != null;`,
		},
		{
			// This PR's own new surface, and the most powerful of the set: it
			// owns commit/rollback for every document the executor manages.
			name:     "ManagedDocumentTransactions",
			typeName: "ManagedDocumentTransactions",
			script:   `return new MCPBridge.Core.Execution.ManagedDocumentTransactions("mine", null).Count;`,
		},
	} {
		t.Run(tc.name, func(t *testing.T) {
			rej := runRejectedScript(t, c, instanceID, documentID, tc.script)
			// The type must be UNNAMEABLE, so this has to fail at compile time.
			// Asserting on the message rather than only on "it failed" is the
			// point: a runtime failure here (a null argument, a Revit refusal)
			// would still fail the call while leaving the type constructible,
			// which is exactly the state this test exists to detect.
			if !strings.Contains(rej.Text, tc.typeName) {
				t.Errorf("rejection does not mention %s, so this may be failing for an unrelated reason; result: %s",
					tc.typeName, rej.Text)
			}
			if !mentionsInaccessible(rej.Text) {
				t.Errorf("%s did not fail as an INACCESSIBLE/unknown type -- it may still be constructible from a script; result: %s",
					tc.typeName, rej.Text)
			}
		})
	}

	// The connector is still usable afterwards: these are ordinary compile
	// rejections, so the ambient transaction unwinds cleanly.
	out := runScript(t, c, instanceID, documentID, `return Document.Title;`)
	if out.Status != "success" {
		t.Errorf("instance unusable after the rejections: status=%q output=%s", out.Status, out.Output)
	}
}

// mentionsInaccessible reports whether a compile rejection failed because the
// named type could not be seen. Roslyn words this differently depending on
// whether the type is inaccessible (CS0122) or the namespace no longer resolves
// at all (CS0234/CS0246), and which one a given type produces is an
// implementation detail of where it sits -- so match the CLASS of error rather
// than one exact code, per this project's "pin topics, not the mechanism of the
// day" rule.
func mentionsInaccessible(text string) bool {
	for _, marker := range []string{"CS0122", "CS0234", "CS0246", "inaccessible", "does not exist"} {
		if strings.Contains(text, marker) {
			return true
		}
	}
	return false
}
