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

// TestConnectorCapabilitiesAreNotReachableThroughACallback pins the fix for the
// SECOND instance of the same bypass class, found by a second independent review
// one round AFTER the first was "fixed" -- which is the whole reason this test
// exists as its own function rather than another row in the table above.
//
// WHY THE ROUND-1 CASES DID NOT CATCH IT: every one of them writes
// `new <ConcreteType>()`, so making the concrete adapters `internal` made them
// all pass. But RevitScriptExecutionHandler was still a PUBLIC type with a
// public constructor and a public Execute(UIApplication) whose body hands a real
// RevitUiApplicationAdapter -- typed as the then-public IUiApplicationAdapter --
// straight to ARBITRARY CALLER-SUPPLIED code:
//
//	class Grab : MCPBridge.RevitAdapter.IScriptExecutionCallback {
//	    public MCPBridge.RevitAdapter.IUiApplicationAdapter A;
//	    public void OnExecute(MCPBridge.RevitAdapter.IUiApplicationAdapter a) => A = a;
//	}
//	var g = new Grab();
//	new MCPBridge.RevitAdapter.RevitScriptExecutionHandler(g).Execute(UIApplication);
//	var doc = ((MCPBridge.RevitAdapter.IDocumentCreationSource)g.A).CreateProjectDocument(null);
//	doc.CreateTransaction("bypass").Start();   // real, unmanaged, untracked
//
// A Roslyn script submission can declare types, so the script supplies the
// callback itself and never has to NAME an internal type -- every type in that
// snippet was public. So the round-1 fix did not block it, and the round-1 tests
// would have kept passing for as long as the hole stayed open.
//
// THE RULE THIS PINS, restated correctly: a public type in MCPBridge.Core /
// MCPBridge.RevitAdapter must neither BE an adapter/adapter-producing type NOR
// RETURN OR YIELD one -- directly, or through a caller-supplied callback or
// delegate. "Interfaces are safe to leave public because a script can never
// obtain one" was the round-1 wording and it is false; the adapter interfaces
// are internal now too, which is what makes the capability itself unnameable.
func TestConnectorCapabilitiesAreNotReachableThroughACallback(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	script := `
class Grab : MCPBridge.RevitAdapter.IScriptExecutionCallback {
    public MCPBridge.RevitAdapter.IUiApplicationAdapter Captured;
    public void OnExecute(MCPBridge.RevitAdapter.IUiApplicationAdapter a) { Captured = a; }
}
var g = new Grab();
new MCPBridge.RevitAdapter.RevitScriptExecutionHandler(g).Execute(UIApplication);
var src = (MCPBridge.RevitAdapter.IDocumentCreationSource)g.Captured;
var doc = src.CreateProjectDocument(null);
var tx = doc.CreateTransaction("bypass");
tx.Start();
return "captured an adapter through a callback";`

	rej := runRejectedScript(t, c, instanceID, documentID, script)

	// Must fail at COMPILE time as an accessibility/unknown-type problem. A
	// runtime failure here (a null, a Revit refusal) would mean every type in
	// the snippet is still nameable and the capture still constructible --
	// exactly the state this test exists to detect, and exactly how the hole
	// survived round 1.
	if !mentionsInaccessible(rej.Text) {
		t.Errorf("the callback-capture script did not fail as an INACCESSIBLE/unknown type -- the capability may still be reachable; result: %s",
			rej.Text)
	}

	// And it must fail on the CAPTURE machinery itself, not incidentally on
	// something else in the snippet.
	mentionsCaptureType := false
	for _, name := range []string{"IScriptExecutionCallback", "IUiApplicationAdapter", "RevitScriptExecutionHandler", "IDocumentCreationSource"} {
		if strings.Contains(rej.Text, name) {
			mentionsCaptureType = true
			break
		}
	}
	if !mentionsCaptureType {
		t.Errorf("rejection names none of the callback-capture types, so it may be failing for an unrelated reason; result: %s", rej.Text)
	}

	// The connector is still usable afterwards: an ordinary compile rejection.
	out := runScript(t, c, instanceID, documentID, `return Document.Title;`)
	if out.Status != "success" {
		t.Errorf("instance unusable after the rejection: status=%q output=%s", out.Status, out.Output)
	}
}

// TestConfirmationTierCannotBeSelfGranted pins the fix for a THIRD-round review
// finding. Unlike the two cases above it never touched check 1 (transaction
// construction stayed unconditionally refused throughout, in every shape); what
// it defeated is check 2, the confirmation-gated tier -- which exists precisely
// so that Document.Close/Save/SaveAs/SynchronizeWithCentral require the REQUEST
// to opt in, not the script.
//
// THE BYPASS, with no reflection and no internal type named:
//
//	var g = (MCPBridge.Core.Execution.ScriptGlobals)((System.Action<string,string>)Publish).Target;
//	new MCPBridge.Core.Execution.RoslynScriptRunner()
//	    .RunAsync("<any script>", g, CancellationToken, confirmLifecycleActions: true)
//	    .GetAwaiter().GetResult();
//
// Two public surfaces compose into the hole. ScriptGlobals must stay public
// (Roslyn binds a script's globals by name against a public type), and
// Delegate.Target is public on every delegate -- so a method-group delegate over
// any ScriptGlobals instance member hands the script the LIVE globals object
// with no reflection at all. RoslynScriptRunner was then public, with a public
// constructor and a public RunAsync whose confirmLifecycleActions parameter the
// script simply passes `true` for, starting a nested run that self-grants the
// tier regardless of what the outer execute_script request asked for.
//
// THE FIX, same shape as rounds 1 and 2: RoslynScriptRunner is internal, so the
// nested-run entry point is unnameable from script scope and this fails to
// COMPILE. The Target trick still works and is still harmless on its own --
// what mattered was that it reached a capability, not that it reached an object.
func TestConfirmationTierCannotBeSelfGranted(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	// The nested script BINDS Document.Save as a method group and never invokes
	// it -- the same convention as TestLifecycleGateCoversTheNewlyAddedMembers,
	// for the same reason: detection fires on the reference exactly as on a
	// call, and actually saving would write a real model to disk. So a run that
	// reports Success=True here has demonstrably been granted the gated tier
	// without saving anything.
	script := `
var g = (MCPBridge.Core.Execution.ScriptGlobals)((System.Action<string, string>)Publish).Target;
var runner = new MCPBridge.Core.Execution.RoslynScriptRunner();
var outcome = runner.RunAsync(
    "System.Action f = Document.Save; return f != null ? \"gated member bound\" : \"null\";",
    g,
    CancellationToken,
    true).GetAwaiter().GetResult();
return "nested run Success=" + outcome.Success + " returned=" + outcome.ReturnValue;`

	// Sent WITHOUT confirm_lifecycle_actions, deliberately: the whole claim is
	// that the request never opted in.
	rej := runRejectedScript(t, c, instanceID, documentID, script)

	// Must fail at COMPILE time on the runner itself. A runtime failure would
	// mean the nested-run entry point is still constructible and callable from a
	// script -- exactly the state this test exists to detect.
	if !strings.Contains(rej.Text, "RoslynScriptRunner") {
		t.Errorf("rejection does not mention RoslynScriptRunner, so it may be failing for an unrelated reason; result: %s", rej.Text)
	}
	if !mentionsInaccessible(rej.Text) {
		t.Errorf("the self-grant script did not fail as an INACCESSIBLE/unknown type -- a script may still be able to start a nested run with confirmLifecycleActions: true; result: %s", rej.Text)
	}

	// The instance is still usable: an ordinary compile rejection.
	out := runScript(t, c, instanceID, documentID, `return Document.Title;`)
	if out.Status != "success" {
		t.Errorf("instance unusable after the rejection: status=%q output=%s", out.Status, out.Output)
	}
}

// TestScriptCannotTamperWithDialogSuppression pins the second third-round
// finding: ActiveDialogContext was a PUBLIC static class whose mutating members
// a script could call by name, with no internal type involved and no reflection.
//
// Two concrete harms, neither of which touches the transaction boundary:
//
//   - ActiveDialogContext.ClearActive() mid-script switches dialog
//     auto-suppression off (DialogSuppressionHandler answers a dialog only while
//     IsActive), so the next real modal dialog DISPLAYS and blocks the very UI
//     thread the script is running on -- wedging Revit with no guarantee a human
//     is there to click it. One line of script, denial of service.
//   - ActiveDialogContext.DrainRecorded() mid-script empties the recorded
//     dialog notices before TransactionScriptExecutor drains them at the end of
//     the run, erasing the PRD §01 record of which dialogs were auto-answered
//     and how -- i.e. defeating "never handled invisibly" while the run still
//     reports success.
//
// THE FIX: the class is internal, reachable only through InternalsVisibleTo from
// MCPBridge.AddIn (DialogSuppressionHandler) and MCPBridge.Core.Tests, so every
// script below fails to COMPILE. The read-only members went internal with it
// rather than being kept public -- nothing outside those assemblies wanted them,
// and this codebase's rule is that a public type in Core/RevitAdapter is a
// script-reachable one.
//
// The DoS case is deliberately proven WITHOUT triggering a dialog afterwards: a
// harness that actually wedged the live UI thread would need a human to rescue
// it. The observability case is safe to prove end to end, and does.
func TestScriptCannotTamperWithDialogSuppression(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	for _, tc := range []struct{ name, script string }{
		{
			// The denial-of-service route: suppression off, mid-run.
			name:   "ClearActive",
			script: `MCPBridge.Core.Execution.ActiveDialogContext.ClearActive(); return "suppression disabled";`,
		},
		{
			// The observability route: auto-answer a real dialog, then erase the
			// evidence before the executor collects it.
			name: "DrainRecorded",
			script: `
Autodesk.Revit.UI.TaskDialog.Show("MCPBridge harness", "drain probe");
var erased = MCPBridge.Core.Execution.ActiveDialogContext.DrainRecorded().Count;
return "erased " + erased + " dialog notice(s)";`,
		},
		{
			// Setting the override policy is a mutation too: it would let a
			// script install answers for dialogs raised by a LATER run, since
			// the executor only replaces this at the start of one.
			name:   "SetActive",
			script: `MCPBridge.Core.Execution.ActiveDialogContext.SetActive(new System.Collections.Generic.Dictionary<string, int>()); return "policy replaced";`,
		},
	} {
		t.Run(tc.name, func(t *testing.T) {
			rej := runRejectedScript(t, c, instanceID, documentID, tc.script)
			if !strings.Contains(rej.Text, "ActiveDialogContext") {
				t.Errorf("rejection does not mention ActiveDialogContext, so it may be failing for an unrelated reason; result: %s", rej.Text)
			}
			if !mentionsInaccessible(rej.Text) {
				t.Errorf("%s did not fail as an INACCESSIBLE/unknown type -- a script may still be able to mutate the dialog context; result: %s", tc.name, rej.Text)
			}
		})
	}

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
