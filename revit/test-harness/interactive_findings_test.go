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

// This file weaves live findings from an interactive test session (working
// through a range of real Revit tasks by hand via execute_script, not
// pre-scripted corpus cases) into the regression suite. The session surfaced
// several gaps this suite's existing, more mechanical coverage did not catch
// -- not because the mechanisms were untested, but because nothing exercised
// them the way a real multi-step agent session actually does: routing a
// later call's document_id directly at a document found in list_instances,
// building a multi-story model from scratch, placing a room on a
// script-created level, reassigning a live sheet's title block. Each test
// below traces back to a specific numbered issue filed from that session;
// see each issue for the full narrative.
//
// UPDATE: by the time this file was wired into a live run, issues #113,
// #114, #116, #117 and #118 had already been fixed and merged (see each
// test's own comment for how that changed what it pins). #115's stairs gap
// remains open.
//
// The crash-triggering MUTATION behind issue #113 (reassigning a placed
// sheet's ViewSheet.SheetTitleBlockId to a symbol from a different Family)
// is deliberately never exercised anywhere in this file. Reproducing a
// crash would take down the shared, long-lived Revit session every other
// suite in this package runs against. TestSheetTitleBlockRecreateWorkaroundIsSafe
// below pins the safe alternative that was used to complete the task instead.

// createAndAwaitDocumentID creates a blank project document and waits on
// list_instances' live snapshot push (issue #30) for its routable tmp- id to
// appear, returning both. Takes t explicitly rather than closing over an
// enclosing test's *testing.T -- every caller here runs it from inside a
// t.Run subtest goroutine, and a closure capturing the parent T would call
// FailNow on the wrong goroutine (see git history: an earlier version of
// this file did exactly that and produced "subtest may have called FailNow
// on a parent test" instead of its own diagnostic, silently skipping every
// subtest after the first failure).
func createAndAwaitDocumentID(t *testing.T, c *mcpclient.Client, instanceID, documentID string) (title, tmpDocID string) {
	t.Helper()
	out := runScript(t, c, instanceID, documentID, `return Connector.CreateProjectDocument().Title;`)
	if out.Status != "success" {
		t.Fatalf("create failed: status=%q (%s)", out.Status, out.diag())
	}
	title = strings.TrimSpace(out.ReturnValue)
	if title == "" {
		t.Fatalf("created document reported no title; (%s)", out.diag())
	}
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
			for _, d := range inst.Documents {
				if d.Title == title {
					return title, d.DocumentID
				}
			}
		}
		time.Sleep(500 * time.Millisecond)
	}
	t.Fatalf("created document %q never appeared in list_instances", title)
	return "", ""
}

// TestCreatedDocumentCloseRequiresRoutingAwayFromIt is the corrected, live-
// re-verified understanding behind issue #114. The issue was originally
// filed as "CreateProjectDocument leaves permanently open, unclosable
// documents -- a real memory leak", based on a document that stayed open
// across many later calls during the interactive session. Re-testing the
// exact sequence live, before any fix work started, found a narrower and
// different bug: a created document is NOT permanently unclosable. It closes
// cleanly from a later, SEPARATE execute_script call -- but only if that
// call is routed elsewhere (document_id = some other open document) and
// finds the created document by Title via Application.Documents, the same
// pattern this suite's own closeDocumentByTitle already uses. Routing
// document_id DIRECTLY at the created document's own tmp- id -- the most
// natural thing to try, since that id is right there in list_instances --
// re-wraps it in a fresh per-call managed transaction, so Close fails for
// THAT call with the same "any open sub-transaction, transaction or
// transaction group" error the ambient document has always produced. It is
// not stuck afterward: a third call, routed away again, closes it cleanly.
// See issue #114 (comment) for the full live trace this test formalizes --
// #114 was fixed as a documentation correction (skill.md was actively
// telling agents the opposite), not a code change, which is exactly what
// this test's shape is built to keep proving true.
func TestCreatedDocumentCloseRequiresRoutingAwayFromIt(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	t.Run("ClosingByRoutingAwayAndFindingByTitleWorks", func(t *testing.T) {
		title, _ := createAndAwaitDocumentID(t, c, instanceID, documentID)
		// Safety net if the close below fails partway through; guarded so the
		// expected-success path doesn't also log a spurious "close-failed: not
		// found" against a document this subtest already closed itself.
		closed := false
		t.Cleanup(func() {
			if !closed {
				closeDocumentByTitle(t, c, instanceID, documentID, title, "")
			}
		})

		script := fixtureLookupPreamble(title) + `doc.Close(false); return "closed";`
		out := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID, script,
			map[string]any{"confirm_lifecycle_actions": true}))
		if out.Status != "success" || !strings.Contains(out.ReturnValue, "closed") {
			t.Fatalf("closing a created document by routing elsewhere and finding it by Title should succeed; status=%q (%s)", out.Status, out.diag())
		}
		closed = true
	})

	t.Run("ClosingByRoutingDirectlyAtItFailsForThatCallOnlyNotForever", func(t *testing.T) {
		title, tmpDocID := createAndAwaitDocumentID(t, c, instanceID, documentID)
		// Safety net regardless of what this subtest proves -- never leaves a
		// created document open. Guarded by closed so the (expected) success
		// path below doesn't also log a spurious "close-failed: not found" from
		// this cleanup running against an already-closed document.
		closed := false
		t.Cleanup(func() {
			if !closed {
				closeDocumentByTitle(t, c, instanceID, documentID, title, "")
			}
		})

		blocked := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, tmpDocID, `
try {
  Document.Close(false);
  return "closed";
} catch (Autodesk.Revit.Exceptions.InvalidOperationException ex) {
  return "refused: " + ex.Message;
}
`, map[string]any{"confirm_lifecycle_actions": true}))
		if blocked.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", blocked.Status, blocked.diag())
		}
		if !strings.Contains(blocked.ReturnValue, "refused:") {
			t.Fatalf("routing document_id directly at a created document was expected to block Close for that call (a fresh per-call transaction wraps it); if this now succeeds, the footgun described in issue #114 may already be fixed; (%s)", blocked.diag())
		}

		// THE ASSERTION THAT WOULD HAVE CAUGHT THE ORIGINAL "permanently
		// unclosable" framing being wrong: a later, separately-routed call still
		// closes it cleanly.
		recheck := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID,
			fixtureLookupPreamble(title)+`doc.Close(false); return "closed";`,
			map[string]any{"confirm_lifecycle_actions": true}))
		if recheck.Status != "success" || !strings.Contains(recheck.ReturnValue, "closed") {
			t.Fatalf("a created document that failed to close via direct routing should still close cleanly via a later, separately-routed call -- if this now fails too, issue #114's ORIGINAL 'permanently unclosable' framing would actually be correct after all; status=%q (%s)", recheck.Status, recheck.diag())
		}
		closed = true
	})
}

// TestStairsAreCreatableWithSettleOnRequest is the positive assertion that
// replaces the two negative stairs tests this file used to carry, and replacing
// them was mandatory rather than optional: both said so in their own comments,
// because relaxing them instead would leave the suite claiming stairs are
// unreachable after they became reachable.
//
// What they pinned, and why both were needed: issue #115 stated its dead end as
// "no script-reachable path satisfies both no-ambient-transaction-to-start and a
// transaction-to-write". That was wrong, and proving it took two tests -- one for
// the START edge (a managed transaction on the target blocks StairsEditScope.Start)
// and one for the COMMIT edge (with the transaction supplied by a WithTransaction block
// INSIDE the scope, Start and CreateStraightRun both succeed and scope.Commit()
// then refuses: "EditScope cannot be closed, for there is a transaction or
// transaction group still open in the document"). The real blocker was a third
// condition nobody had named -- no transaction may be open when the scope COMMITS
// -- and closing a connector-owned transaction mid-run is the one thing no script
// could do.
//
// Under group-always (#146 Phase 3) the shape is the natural one: nothing is
// open between blocks, so Start() is legal at top level; WithTransaction opens
// a transaction INSIDE the scope so the run can be built, and its closing edge
// -- not tidiness, the load-bearing part -- is what makes scope.Commit() legal
// a line later. (Before Phase 3 this needed a WithoutTransaction escape hatch
// around the whole thing; that member is gone.)
//
// Note the mutation evidence behind the causal claim, recorded when the negative
// test was retired: removing the transaction entirely flipped commitOutcome to
// "committed", so the refusal really was caused by the connector holding one.
func TestStairsAreCreatableWithSettleOnRequest(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
Autodesk.Revit.DB.Level l1 = null, l2 = null;
Connector.WithTransaction(doc, () => {
  l1 = Autodesk.Revit.DB.Level.Create(doc, 0.0);
  l2 = Autodesk.Revit.DB.Level.Create(doc, 12.0);
  doc.Regenerate();
});

int runCount = -1;
int risers = -1;

// No block open here: the document is not modifiable, which is what Start() needs.
var scope = new Autodesk.Revit.DB.StairsEditScope(doc, "harness group-always stairs");
var newStairsId = scope.Start(l1.Id, l2.Id);
long stairsId = newStairsId.Value;

Connector.WithTransaction(doc, () => {
  var line = Autodesk.Revit.DB.Line.CreateBound(
    new Autodesk.Revit.DB.XYZ(0, 0, 0), new Autodesk.Revit.DB.XYZ(20, 0, 0));
  Autodesk.Revit.DB.Architecture.StairsRun.CreateStraightRun(
    doc, newStairsId, line, Autodesk.Revit.DB.Architecture.StairsRunJustification.Center);
});

// The block closed its transaction, so the scope can commit.
scope.Commit(new Preproc());

var stairs = doc.GetElement(new Autodesk.Revit.DB.ElementId(stairsId)) as Autodesk.Revit.DB.Architecture.Stairs;
if (stairs != null) { runCount = stairs.GetStairsRuns().Count; risers = stairs.ActualRisersNumber; }
var stairsInModel = new Autodesk.Revit.DB.FilteredElementCollector(doc)
  .OfClass(typeof(Autodesk.Revit.DB.Architecture.Stairs)).GetElementCount();

return new { stairsId, runCount, risers, stairsInModel, inEditMode = doc.IsInEditMode() };

class Preproc : Autodesk.Revit.DB.IFailuresPreprocessor {
  public Autodesk.Revit.DB.FailureProcessingResult PreprocessFailures(Autodesk.Revit.DB.FailuresAccessor a) {
    a.DeleteAllWarnings();
    return Autodesk.Revit.DB.FailureProcessingResult.Continue;
  }
}
`)
	if out.Status != "success" {
		t.Fatalf("stairs creation was expected to SUCCEED under group-always (#146 Phase 3) -- this is the assertion that proves the feature works end to end; status=%q (%s)", out.Status, out.diag())
	}
	for _, want := range []string{`"runCount":1`, `"stairsInModel":1`, `"inEditMode":false`} {
		if !strings.Contains(out.ReturnValue, want) {
			t.Errorf("wanted %q in the result; (%s)", want, out.diag())
		}
	}
	// Risers are the load-bearing proof that the run was really BUILT rather than an
	// empty stairs element being created and committed: an empty stairs has none.
	if strings.Contains(out.ReturnValue, `"risers":0`) || strings.Contains(out.ReturnValue, `"risers":-1`) {
		t.Errorf("the stairs element exists but has no risers, so the run was never built -- an empty stairs would still satisfy every other assertion here; (%s)", out.diag())
	}
}

// TestWithTransactionRecoversWhenItsBodyThrowsAndTheScriptCatches is the live
// half of RunBody's contract, and it exists because both failures that method
// guards against are reachable only from ORDINARY script code -- a try/catch
// around one API call, falling back when it fails -- yet neither is provable in
// tier 1, where no script can name a Revit type or open a real transaction.
//
// RunBody's own comment names exactly two things a thrown-then-caught body must
// not leave behind, and this one script pins both at once:
//
//  1. THE WEDGE. If the throwing block's transaction is left open, every later
//     WithTransaction on that document throws "a transaction is already open"
//     for the rest of the run. Here the second WithTransaction (block 2) runs
//     immediately after block 1 threw; secondBlockThrew must be false. Remove
//     `entry.Transaction = null` from RunBody's catch and this flips true.
//
//  2. NO SILENT COMMIT OF FAILED WORK. The partial writes of a body that threw
//     must not become permanent -- carried in an open transaction that CommitAll
//     (or a later Settle(keep:true)) makes permanent, while Connector's own
//     summary promises it "commits when the block ends". atThrown must be 0: the
//     thrown block's level is absent from the model.
//
//     This leg pins that END STATE, and deliberately does not claim a per-line
//     mutation the way (1) does. Removing ONLY RunBody's explicit SafeRollBack
//     would NOT flip it: the SafeDispose a line later disposes a still-open Revit
//     transaction, which Revit itself rolls back, so the level is absent either
//     way (confirmed against RevitTransactionAdapter.Dispose -> Transaction.Dispose).
//     The mutation that truly reintroduces a silent commit -- leaving the
//     transaction open AND registered so CommitAll commits it -- trips (1)'s wedge
//     as well, and (1) is where the clean per-line evidence lives. atThrown is
//     kept because "failed work is absent" is a real invariant worth asserting
//     directly, independent of which line achieves it.
//
// atSurvivor==1 is the third leg: recovery is not merely "no crash" but a fully
// usable document -- block 2's write commits normally. All three come from one
// run so the wedge and the rollback are proven against the SAME live state, not
// two documents that happened to behave.
//
// Since #146 Phase 3 the blocks sit at top level: nothing is open between them,
// which is the resting state every document is in.
func TestWithTransactionRecoversWhenItsBodyThrowsAndTheScriptCatches(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
bool caught = false;
bool secondBlockThrew = false;

// Block 1: write, then throw. The SCRIPT catches it -- this is the ordinary
// "try an API, fall back on failure" shape, not an exotic case.
try {
  Connector.WithTransaction(doc, () => {
    Autodesk.Revit.DB.Level.Create(doc, 77.7);
    throw new System.InvalidOperationException("harness: deliberate throw inside WithTransaction");
  });
} catch (System.InvalidOperationException) {
  caught = true;
}

// Block 2: the WEDGE probe. If block 1's transaction was left open, this
// throws "a transaction is already open" and secondBlockThrew goes true.
try {
  Connector.WithTransaction(doc, () => {
    Autodesk.Revit.DB.Level.Create(doc, 88.8);
  });
} catch (System.Exception) {
  secondBlockThrew = true;
}

// Reads need no block. Count each level by its distinctive elevation: the
// thrown block's must be GONE (rolled back), the survivor's PRESENT (committed).
int atThrown = 0, atSurvivor = 0;
foreach (var e in new Autodesk.Revit.DB.FilteredElementCollector(doc)
    .OfClass(typeof(Autodesk.Revit.DB.Level))) {
  var lvl = e as Autodesk.Revit.DB.Level;
  if (lvl == null) continue;
  if (System.Math.Abs(lvl.Elevation - 77.7) < 0.01) atThrown++;
  if (System.Math.Abs(lvl.Elevation - 88.8) < 0.01) atSurvivor++;
}
return new { caught, secondBlockThrew, atThrown, atSurvivor };
`)
	if out.Status != "success" {
		t.Fatalf("the run was expected to SUCCEED -- a caught throw inside WithTransaction is ordinary script control flow, and if the block wedged the transaction open the whole run fails here; status=%q (%s)", out.Status, out.diag())
	}
	if !strings.Contains(out.ReturnValue, `"caught":true`) {
		t.Fatalf("the deliberate throw inside WithTransaction did not propagate to the script's own catch, so this test is not exercising what it claims; (%s)", out.diag())
	}
	if !strings.Contains(out.ReturnValue, `"secondBlockThrew":false`) {
		t.Errorf("a second WithTransaction after the first threw was itself refused -- the throwing block left its transaction open (the WEDGE). This is what RunBody's `entry.Transaction = null` prevents; (%s)", out.diag())
	}
	if !strings.Contains(out.ReturnValue, `"atThrown":0`) {
		t.Errorf("the level created by the body that THREW survived -- failed work was committed, exactly the silent-commit RunBody's rollback exists to stop (Connector's summary promises it commits only when the block ENDS); (%s)", out.diag())
	}
	if !strings.Contains(out.ReturnValue, `"atSurvivor":1`) {
		t.Errorf("the level created by the recovered second block is missing -- recovery from a caught throw is not merely 'no crash', the document must stay fully writable afterward; (%s)", out.diag())
	}
}

// TestSettleMakesLifecycleActionsReachableInTheSameRun pins the capability
// Connector.Settle exists for, and the notice PRD §01 requires alongside it.
//
// THE GAP IT CLOSES. Revit refuses Close/Save/SaveAs while ANY transaction or
// transaction GROUP is open on the document -- probed live during #115's triage,
// and unlike the EditScope case the group really is the bar there. Under
// always-open that made a document unclosable and unsaveable for the whole run
// that touched it, which is where issue #114's five orphaned documents came from.
// Settle ends the group in the direction the SCRIPT states, and the direction has
// to be stated rather than inferred: the connector cannot see doc.Close() at all
// (ScriptApiDenylist is a compile-time walk that gates but cannot intercept), and
// neither DocumentSavingAs nor DocumentClosing fires while a group is open,
// because Revit's transaction-phase check precedes event dispatch.
//
// WHY THIS IS TIER 2 AND NOT TIER 1. Reaching Settle from a script needs
// IExistingDocumentSource, which only the live adapter implements, so tier 1 can
// cover the state machine and the notice's WORDING but structurally cannot prove
// either that Revit then permits the lifecycle call or that the notice reaches
// the caller. Both of those are asserted here and nowhere else.
func TestSettleMakesLifecycleActionsReachableInTheSameRun(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	t.Run("DiscardThenCloseInTheSameRun", func(t *testing.T) {
		title, _ := createAndAwaitDocumentID(t, c, instanceID, documentID)
		closed := false
		t.Cleanup(func() {
			if !closed {
				closeDocumentByTitle(t, c, instanceID, documentID, title, "")
			}
		})

		out := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID,
			fixtureWritePreamble(title)+`
Connector.WithTransaction(doc, () => { Autodesk.Revit.DB.Level.Create(doc, 30.0); });
Connector.Settle(doc, false);
doc.Close(false);
return "closed";
`, map[string]any{"confirm_lifecycle_actions": true}))
		if out.Status != "success" || !strings.Contains(out.ReturnValue, "closed") {
			t.Fatalf("Settle(discard) then Close in the SAME run was expected to succeed (#132) -- this is the capability that did not exist under always-open; status=%q (%s)", out.Status, out.diag())
		}
		closed = true

		// §01: settling is invisible to the script but changes what the run means,
		// so it must be reported. Asserted on the CODE, not the prose -- a message
		// substring would keep passing if the record's code were wrong on the wire,
		// which is the failure mode rejectedScript's own comment records.
		if !hasNoticeCode(out, "document-settled-discarded") {
			t.Errorf("no `document-settled-discarded` notice reached the caller; notices: %+v", out.Notices)
		}
	})

	t.Run("KeepThenSaveAsInTheSameRun", func(t *testing.T) {
		const savedPath = `C:\dev\fixtures\settle-saveas-probe.rvt`
		const savedTitle = "settle-saveas-probe"
		title, _ := createAndAwaitDocumentID(t, c, instanceID, documentID)
		// After SaveAs the document is known by the FILE's basename, not its old
		// tmp- title, so cleanup has to chase the new one -- and delete the file.
		t.Cleanup(func() { closeDocumentByTitle(t, c, instanceID, documentID, savedTitle, savedPath) })

		out := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID,
			fixtureWritePreamble(title)+`
try { System.IO.File.Delete(@"C:\dev\fixtures\settle-saveas-probe.rvt"); } catch {}
Connector.WithTransaction(doc, () => { Autodesk.Revit.DB.Level.Create(doc, 40.0); });
Connector.Settle(doc, true);
doc.SaveAs(@"C:\dev\fixtures\settle-saveas-probe.rvt");
return new {
  saved = System.IO.File.Exists(@"C:\dev\fixtures\settle-saveas-probe.rvt"),
  title = doc.Title
};
`, map[string]any{"confirm_lifecycle_actions": true}))
		if out.Status != "success" {
			t.Fatalf("Settle(keep) then SaveAs in the SAME run was expected to succeed (#132); status=%q (%s)", out.Status, out.diag())
		}
		if !strings.Contains(out.ReturnValue, `"saved":true`) {
			t.Fatalf("SaveAs reported no error but wrote no file; (%s)", out.diag())
		}
		if !hasNoticeCode(out, "document-settled-kept") {
			t.Errorf("no `document-settled-kept` notice reached the caller -- settling with keep:true makes prior writes permanent and §01 does not permit that silently; notices: %+v", out.Notices)
		}
	})
}

// hasNoticeCode reports whether the run carried a §01 notice with this code.
// Matching on `code` rather than on message text is deliberate: the codes are the
// part skill.md tells an agent to branch on, and a message-substring assertion
// keeps passing while the code on the wire is wrong -- the exact hole
// rejectedScript's own comment records finding in this suite before.
func hasNoticeCode(out executeScriptOut, code string) bool {
	for _, n := range out.Notices {
		if n.Code == code {
			return true
		}
	}
	return false
}

// TestWriteOutsideATransactionCarriesAnActionableCode pins the §01 remedy
// mapping settle-on-request needs (#132).
//
// WHY IT HAS TO BE A LIVE TEST. Revit offers no pre-write hook, so a script that
// forgets to wrap a write is inevitable, and Revit's own message ("Attempt to
// modify the model outside of transaction") names no way out. The connector maps
// that to `script-write-outside-transaction` plus a remedy -- but it matches on
// the exception's FULL TYPE NAME as a string, because a type pattern would force
// RevitAPI.dll to resolve when the dispatcher method is JITed and break the
// entire tier-1 host. A string match FAILS OPEN: a typo does not error, the
// mapping just never fires and the agent silently gets the generic code back.
// Nothing at tier 1 can catch that, because tier 1 cannot construct the
// exception. This test is the only thing standing between that string and a
// silent regression.
func TestWriteOutsideATransactionCarriesAnActionableCode(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	// fixtureLookupPreamble deliberately, NOT fixtureWritePreamble: the whole point
	// is a document that is found but never opened for writing.
	rejected := runRejectedScript(t, c, instanceID, documentID, fixtureLookupPreamble(fixtureTitle)+`
Autodesk.Revit.DB.Level.Create(doc, 50.0);
return "should not get here";
`)

	if rejected.Error.Code != "script-write-outside-transaction" {
		t.Fatalf("a write with no transaction open must map to `script-write-outside-transaction`, got %q -- if this is `script-execution-failed`, the full type name in RequestDispatcher.IsModificationOutsideTransaction no longer matches Revit's and the mapping is failing open; message: %s", rejected.Error.Code, rejected.Error.Message)
	}
	if len(rejected.Error.Remedy) == 0 {
		t.Fatalf("the code is right but no remedy was carried, which is the half that tells an agent what to do; %+v", rejected.Error)
	}
	// The remedy has to name the member that fixes it, not merely describe the problem.
	joined := strings.Join(rejected.Error.Remedy, " ")
	if !strings.Contains(joined, "Connector.WithTransaction") {
		t.Errorf("the remedy does not name Connector.WithTransaction, so it does not actually tell an agent what to do: %q", joined)
	}
}

// TestRoomOnScriptCreatedLevelNeedsComputationHeight pins the live-confirmed
// gotcha behind issue #118: a Level created via Level.Create defaults its
// Room Computation Height to 0 -- exactly the level's own elevation, which
// is also exactly where a wall created with that level as its base
// constraint starts. Placing a room at that computation height can then find
// ZERO boundary loops (not merely a wrong area) even though the surrounding
// walls are geometrically closed and correctly flagged room-bounding --
// confirmed against Autodesk's own KB on computation-height-at-the-boundary,
// in the interactive session this test is drawn from. #118 FIXED this as a
// documentation correction (landed alongside #114): the trap and its recipe
// are now written down in skill.md/caveats.md rather than any default
// behavior changing.
//
// THE GAP DOES NOT RELIABLY REPRODUCE under THIS test's own construction,
// live-tested against three different elevations (10.0, matching the
// original session's exact value; 6100.0; 6200.0) -- all three came back
// properly enclosed (loops = 1) against a document built here via
// createBlankFixtureDocument (Connector.CreateProjectDocument, which uses
// Revit's own DefaultProjectTemplate). At least one uncontrolled variable
// from the original repro is not reproduced here: that session's document
// was created via the RAW, unmanaged Application.NewProjectDocument(UnitSystem)
// path with no template, not the templated, managed path this fixture
// helper uses -- not re-tested here, since the un-saved intermediate from
// that path is read-only (per get_skills) and would need its own separate
// SaveAs/OpenAndActivateDocument write path to host Wall.Create/NewRoom.
// The original repro also had a SECOND level above the room's level (this
// fixture has only the one script-created level) and walls created with an
// implicit default type height rather than an explicit one, either of which
// could independently matter. So "the one remaining difference" would
// overclaim; more than one variable differs, and which (if any) is load-
// bearing has not been isolated.
//
// Given that, DefaultComputationHeightProbe below is deliberately a PROBE,
// not a pass/fail gate: it reports what it finds rather than asserting a
// specific outcome, because the reproduction has not been pinned down
// precisely enough to assert on with confidence. SettingComputationHeightInsideTheWallBodyWorks
// still asserts the known-good recipe produces a valid, non-zero-area room
// as a hard PASS -- but since the probe above shows the room already comes
// back enclosed even WITHOUT setting computation height under this
// construction, that assertion is not currently a controlled negative-case
// regression pin (it would not go red if LEVEL_ROOM_COMPUTATION_HEIGHT
// stopped working here, since the room is enclosed either way). A true A/B
// -- the same footprint, at the same elevation, once with and once without
// the recipe, compared directly -- needs two levels Revit will accept at
// the same elevation in one document, which was not solved here; tracked as
// a known follow-up rather than attempted under time pressure.
func TestRoomOnScriptCreatedLevelNeedsComputationHeight(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	const buildRoom = `
var level = Autodesk.Revit.DB.Level.Create(doc, %s);
var pts = new Autodesk.Revit.DB.XYZ[] {
  new Autodesk.Revit.DB.XYZ(0, 0, level.Elevation), new Autodesk.Revit.DB.XYZ(20, 0, level.Elevation),
  new Autodesk.Revit.DB.XYZ(20, 20, level.Elevation), new Autodesk.Revit.DB.XYZ(0, 20, level.Elevation),
};
for (int i = 0; i < pts.Length; i++) {
  var line = Autodesk.Revit.DB.Line.CreateBound(pts[i], pts[(i + 1) %% pts.Length]);
  Autodesk.Revit.DB.Wall.Create(doc, line, level.Id, false);
}
%s
var room = doc.Create.NewRoom(level, new Autodesk.Revit.DB.UV(10, 10));
doc.Regenerate();
var loops = room.GetBoundarySegments(new Autodesk.Revit.DB.SpatialElementBoundaryOptions()).Count;
return "area = " + room.Area + "; loops = " + loops + ";";
`

	t.Run("DefaultComputationHeightProbe", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+
			withTx(fmt.Sprintf(buildRoom, "10.0", "")))
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		if strings.Contains(out.ReturnValue, "loops = 0;") {
			t.Logf("reproduced the gap live: default computation height produced an unenclosed room (issue #118); (%s)", out.diag())
		} else {
			t.Logf("did NOT reproduce the gap against a Connector.CreateProjectDocument-templated fixture (see this test's doc comment for the uncontrolled variables); (%s)", out.diag())
		}
	})

	t.Run("SettingComputationHeightInsideTheWallBodyWorks", func(t *testing.T) {
		setHeight := `level.get_Parameter(Autodesk.Revit.DB.BuiltInParameter.LEVEL_ROOM_COMPUTATION_HEIGHT).Set(4.0);`
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+
			withTx(fmt.Sprintf(buildRoom, "6200.0", setHeight)))
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		if strings.Contains(out.ReturnValue, "loops = 0;") {
			t.Fatalf("the known-good recipe (computation height set inside the wall body) failed to produce an enclosed room; (%s)", out.diag())
		}
	})
}

// TestCreatedProjectDocumentHasNoWindow pins the current, surprising
// behavior behind issue #118: Connector.CreateProjectDocument produces a
// real, addressable Document, but it never gets a UI window and never
// becomes the active document -- confirmed live via UIDocument being null
// when a script is routed directly at it, and it showing active:false in
// list_instances even when it is the only open document. A human watching
// Revit's actual window sees nothing change. FIXED as a documentation
// correction (#118): this remains the actual, intended behavior (a created
// document is deliberately headless), now written down clearly instead of
// silently surprising whoever hits it next -- so this test still pins the
// SAME behavior as before, just no longer an undocumented trap.
func TestCreatedProjectDocumentHasNoWindow(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	title, tmpDocID := createAndAwaitDocumentID(t, c, instanceID, documentID)
	t.Cleanup(func() { closeDocumentByTitle(t, c, instanceID, documentID, title, "") })

	// Re-check active:false from the same list_instances snapshot
	// createAndAwaitDocumentID already confirmed the document in, rather than
	// a second round trip -- both properties (routable id, not active) are
	// asserted from data already in hand.
	raw, err := c.CallTool("list_instances", map[string]any{}, 10*time.Second)
	if err != nil {
		t.Fatalf("list_instances: %v", err)
	}
	instances := decodeToolResult[listInstancesOut](t, raw)
	var found bool
	for _, inst := range instances.Instances {
		if inst.InstanceID != instanceID {
			continue
		}
		for _, d := range inst.Documents {
			if d.DocumentID == tmpDocID {
				found = true
				if d.Active {
					t.Errorf("created document unexpectedly reported active:true in list_instances -- if CreateProjectDocument now opens a real window, issue #118 may already be addressed; this test should then assert active:true instead of failing")
				}
			}
		}
	}
	if !found {
		t.Fatalf("created document %q (id %s) no longer appears in list_instances", title, tmpDocID)
	}

	out := runScript(t, c, instanceID, tmpDocID, `return UIDocument == null ? "no-window" : "has-window";`)
	if out.Status != "success" {
		t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
	}
	if !strings.Contains(out.ReturnValue, "no-window") {
		t.Errorf("expected UIDocument to be null for a document CreateProjectDocument produced -- if this now has a window, issue #118 may already be addressed; this test should then assert has-window instead; (%s)", out.diag())
	}
}

// issue #117 (return values not serializing -- a List<anonymous type> came
// back as its raw CLR type name, e.g.
// "System.Collections.Generic.List`1[<>f__AnonymousType0#1`2[...]]") was
// fixed and merged with its own dedicated, more thorough live test
// (TestReturnValueSerialization in return_value_test.go) by the time this
// file was run against a live instance -- it pins the exact issue #117
// script shape, the real JSON-array assertion, the honest-fallback case for
// a non-serializable Revit Element, and the output/return_value field
// split. No test is duplicated here; see that file instead.

// TestSheetTitleBlockRecreateWorkaroundIsSafe pins the SAFE workaround
// discovered live for issue #113 (a real Revit fatal crash triggered by
// reassigning an already-placed sheet's ViewSheet.SheetTitleBlockId to a
// symbol from a DIFFERENT title block Family). This suite deliberately never
// exercises the crash-triggering mutation itself -- doing so would crash the
// shared, long-lived Revit session this harness (and every other suite in
// this package) runs against, the same reasoning
// TestLifecycleGateRequiresConfirmation's comment gives for binding a method
// group rather than invoking members with irreversible real-world effects.
// Instead, this pins that creating a NEW sheet with the desired title block
// type from the start -- never touching a live instance's
// SheetTitleBlockId -- works cleanly and actually carries the requested
// type, which is the pattern that completed the interactive session's task
// after the crash. #113 landed a documentation correction (the title-block
// id trap, per its own commit) but the crash path itself was not guarded in
// code, so this test's own reasoning for never exercising the mutation
// still applies unchanged even though the issue itself is closed.
func TestSheetTitleBlockRecreateWorkaroundIsSafe(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	out := runScript(t, c, instanceID, documentID, "using System.Linq;\n"+fixtureWritePreamble(fixtureTitle)+withTx(`
// FloorPlan is a built-in ViewFamily every valid Revit install ships a
// ViewFamilyType for, unlike a title block symbol (which depends on what the
// project template happened to load) -- First() is fine here for exactly
// the reason FirstOrDefault()+skip is used for tbType below.
var vft = new Autodesk.Revit.DB.FilteredElementCollector(doc)
    .OfClass(typeof(Autodesk.Revit.DB.ViewFamilyType)).Cast<Autodesk.Revit.DB.ViewFamilyType>()
    .First(x => x.ViewFamily == Autodesk.Revit.DB.ViewFamily.FloorPlan);
var level = Autodesk.Revit.DB.Level.Create(doc, 6300.0);
var plan = Autodesk.Revit.DB.ViewPlan.Create(doc, vft.Id, level.Id);

var tbType = new Autodesk.Revit.DB.FilteredElementCollector(doc)
    .OfClass(typeof(Autodesk.Revit.DB.FamilySymbol)).OfCategory(Autodesk.Revit.DB.BuiltInCategory.OST_TitleBlocks)
    .Cast<Autodesk.Revit.DB.FamilySymbol>().FirstOrDefault();
if (tbType == null) { return (object)"no-titleblock-loaded"; }
if (!tbType.IsActive) { tbType.Activate(); }

var sheet = Autodesk.Revit.DB.ViewSheet.Create(doc, tbType.Id);
var viewport = Autodesk.Revit.DB.Viewport.Create(doc, sheet.Id, plan.Id, new Autodesk.Revit.DB.XYZ(0.5, 0.5, 0));

// The property the #113 workaround actually depends on: the sheet's placed
// title block instance is the REQUESTED type, not merely some type. This is
// what would silently regress if ViewSheet.Create ever stopped honoring the
// symbol it was given.
var placedTitleBlock = new Autodesk.Revit.DB.FilteredElementCollector(doc, sheet.Id)
    .OfCategory(Autodesk.Revit.DB.BuiltInCategory.OST_TitleBlocks)
    .FirstOrDefault();
var placedTypeMatches = placedTitleBlock != null && placedTitleBlock.GetTypeId() == tbType.Id;

return new { sheetCreated = sheet != null, viewportCreated = viewport != null, placedTypeMatches = placedTypeMatches };
`))
	if out.Status != "success" {
		t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
	}
	if strings.Contains(out.ReturnValue, "no-titleblock-loaded") {
		t.Skip("no title block family loaded in the fixture document's default template on this machine")
	}
	// return_value is real JSON since issue #117 landed -- a serialized
	// anonymous object, not the old "field = True" ToString() rendering.
	for _, want := range []string{`"sheetCreated":true`, `"viewportCreated":true`, `"placedTypeMatches":true`} {
		if !strings.Contains(out.ReturnValue, want) {
			t.Errorf("wanted %q in return_value; (%s)", want, out.diag())
		}
	}
}

// TestRoutingAwayFromATargetMakesItNonModifiable pins the recipe PR #131 wrote
// into skill.md and caveats.md, which covers two of issue #115's three reported
// symptoms without any code change at all.
//
// THE RULE: the document a call is ROUTED at is modifiable for that whole run,
// because TransactionScriptExecutor opens the ambient managed transaction on it
// before the script compiles and there is no per-call opt-out. Any Revit API
// that manages its own transaction and refuses a modifiable target therefore
// always fails against the routed document -- and succeeds from a run routed at
// some OTHER open document, reaching the target through UIApplication.
//
// WHY THIS EXISTS AS A TEST, not just as prose: #115 was filed after an
// interactive session worked around RequestViewChange with a one-shot
// UIApplication.Idling handler, because nothing wrote the recipe down in
// general form -- caveats.md carried it only in a LoadFamily-shaped form that
// did not cover the case where the target IS the routed document. #114 and #118
// were both fixed as documentation corrections and both got a test here so the
// doc could not silently go stale; this is the same move for #131. Without it,
// a Revit version that changes this behaviour breaks nothing and the docs just
// start lying to agents.
//
// The third subtest deliberately asserts BOTH directions. The refusal alone
// would pass just as well if RequestViewChange had stopped working entirely,
// and the success alone would pass if the modifiability precondition had
// quietly stopped applying -- neither is coverage of the rule on its own.
func TestRoutingAwayFromATargetMakesItNonModifiable(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle, fixtureDocID := createAndAwaitDocumentID(t, c, instanceID, documentID)
	t.Cleanup(func() { closeDocumentByTitle(t, c, instanceID, documentID, fixtureTitle, "") })

	t.Run("TheRoutedDocumentIsModifiableOnlyInsideABlock", func(t *testing.T) {
		// #146 Phase 3: the routed document has the run's GROUP open and no transaction -- readable,
		// not modifiable -- until a WithTransaction block opens one, and not again after it closes.
		out := runScript(t, c, instanceID, fixtureDocID, `
bool before = Document.IsModifiable;
bool inside = Connector.WithTransaction(Document, () => Document.IsModifiable);
bool after = Document.IsModifiable;
return new { before, inside, after };
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		for _, want := range []string{`"before":false`, `"inside":true`, `"after":false`} {
			if !strings.Contains(out.ReturnValue, want) {
				t.Fatalf("wanted %s -- group-always means not modifiable outside a WithTransaction block and modifiable inside it; (%s)", want, out.diag())
			}
		}
	})

	t.Run("TheSameDocumentReachedByTitleFromElsewhereIsNot", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureLookupPreamble(fixtureTitle)+
			`return doc.IsModifiable ? "modifiable" : "not-modifiable";`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		if !strings.Contains(out.ReturnValue, "not-modifiable") {
			t.Fatalf("a document reached by Title from a run routed ELSEWHERE must not be modifiable (nothing manages it); (%s)", out.diag())
		}
	})

	t.Run("RequestViewChangeIsRefusedOnlyInsideABlock", func(t *testing.T) {
		// #146 Phase 3 inverted the old #131 recipe: at the active document, with no block open, the
		// document is not modifiable and RequestViewChange is ACCEPTED; inside a WithTransaction block
		// it is refused with Revit's "modifiable document" message. Both directions asserted, so the
		// rule is pinned rather than either half passing for an unrelated reason. Nothing changes on
		// screen from the refused half; the accepted half's view change is undone by the sibling
		// subtest's own restore logic pattern -- here we request the CURRENT active view, a no-op.
		out := runScript(t, c, instanceID, documentID, `
Autodesk.Revit.DB.View target = null;
foreach (Autodesk.Revit.DB.Element e in new Autodesk.Revit.DB.FilteredElementCollector(Document).OfClass(typeof(Autodesk.Revit.DB.ViewPlan))) {
  var v = (Autodesk.Revit.DB.View)e;
  if (!v.IsTemplate && v.Id != UIDocument.ActiveView.Id) { target = v; break; }
}
if (target == null) { return "no-other-view"; }
string insideBlock;
try {
  Connector.WithTransaction(Document, () => { UIDocument.RequestViewChange(target); });
  insideBlock = "accepted";
} catch (Autodesk.Revit.Exceptions.InvalidOperationException ex) {
  insideBlock = "refused: " + ex.Message;
}
string outsideBlock;
try {
  UIDocument.RequestViewChange(UIDocument.ActiveView);   // the active view itself: accepted means no visible change
  outsideBlock = "accepted";
} catch (Autodesk.Revit.Exceptions.InvalidOperationException ex) {
  outsideBlock = "refused: " + ex.Message;
}
return insideBlock + " | " + outsideBlock;
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		if strings.Contains(out.ReturnValue, "no-other-view") {
			t.Skip("active document has fewer than two non-template plan views; nothing to switch between")
		}
		if !strings.HasPrefix(strings.Trim(out.ReturnValue, `"`), "refused:") {
			t.Fatalf("RequestViewChange INSIDE a WithTransaction block was expected to be REFUSED (the document is modifiable there); (%s)", out.diag())
		}
		if !strings.HasSuffix(strings.Trim(out.ReturnValue, `"`), "| accepted") {
			t.Fatalf("RequestViewChange OUTSIDE a block at the active document was expected to be ACCEPTED under group-always -- if it is refused, a transaction is open when none should be; (%s)", out.diag())
		}
	})

	t.Run("AndTheViewActuallyChangesWhenRoutedAway", func(t *testing.T) {
		// IDENTITY, NOT NAME, throughout -- and that is a correction rather than a
		// preference. The first live run of this subtest failed, reporting the view
		// change as "accepted and then dropped". It had not been dropped: the target
		// picked below is the first non-template ViewPlan whose Id differs from the
		// active view, and in the real fixture model that is "L1 - Architectural"
		// (CeilingPlan, id 36) -- a genuinely different view that happens to share
		// its NAME with the active "L1 - Architectural" (FloorPlan, id 32). The
		// change worked; comparing names could not see it.
		//
		// That is caveats.md's "the test faithfully checks a PROXY for the property,
		// not the property" -- and it survived manual verification precisely because
		// the by-hand probe excluded candidates by name, so it happened to pick "L2"
		// and agreed with itself. A view's identity is its ElementId; its name is not
		// unique across view types in any real model.
		before := runScript(t, c, instanceID, fixtureDocID,
			`return UIApplication.ActiveUIDocument.ActiveView.Id.Value.ToString();`)
		if before.Status != "success" {
			t.Fatalf("reading the active view: status=%q (%s)", before.Status, before.diag())
		}
		originalViewID := strings.TrimSpace(before.ReturnValue)
		if originalViewID == "" {
			t.Fatalf("active view reported an empty id; (%s)", before.diag())
		}
		// Restore whatever the shared session was looking at, whichever way this
		// subtest ends. Routed at the fixture document for the same reason the
		// change itself is: the active document must not be modifiable.
		t.Cleanup(func() {
			restore := runScript(t, c, instanceID, fixtureDocID, `
var uidoc = UIApplication.ActiveUIDocument;
var back = uidoc.Document.GetElement(new Autodesk.Revit.DB.ElementId(`+originalViewID+`L)) as Autodesk.Revit.DB.View;
if (back == null) { return "original view not found"; }
uidoc.RequestViewChange(back);
return "restored";
`)
			if !strings.Contains(restore.ReturnValue, "restored") {
				t.Logf("WARNING: could not restore the session's original active view (id %s); (%s)", originalViewID, restore.diag())
			}
		})

		changed := runScript(t, c, instanceID, fixtureDocID, `
var uidoc = UIApplication.ActiveUIDocument;
Autodesk.Revit.DB.View target = null;
foreach (Autodesk.Revit.DB.Element e in new Autodesk.Revit.DB.FilteredElementCollector(uidoc.Document).OfClass(typeof(Autodesk.Revit.DB.ViewPlan))) {
  var v = (Autodesk.Revit.DB.View)e;
  if (!v.IsTemplate && v.Id != uidoc.ActiveView.Id) { target = v; break; }
}
if (target == null) { return "no-other-view"; }
uidoc.RequestViewChange(target);
return "requested " + target.Id.Value;
`)
		if changed.Status != "success" {
			t.Fatalf("RequestViewChange from a run routed AWAY from the active document was expected to succeed (issue #115 triage verified this live); status=%q (%s)", changed.Status, changed.diag())
		}
		if strings.Contains(changed.ReturnValue, "no-other-view") {
			t.Skip("active document has fewer than two non-template plan views; nothing to switch between")
		}

		// RequestViewChange applies only once the API context ends, so the proof is
		// necessarily a SECOND call -- asserting on the first would pass even if the
		// request were silently dropped. Polled rather than read once, because the
		// change lands on Revit's idle loop some time after our script returns and a
		// single immediate re-read races it.
		deadline := time.Now().Add(20 * time.Second)
		lastID := originalViewID
		switched := false
		for time.Now().Before(deadline) {
			after := runScript(t, c, instanceID, fixtureDocID,
				`return UIApplication.ActiveUIDocument.ActiveView.Id.Value.ToString();`)
			if after.Status != "success" {
				t.Fatalf("re-reading the active view: status=%q (%s)", after.Status, after.diag())
			}
			lastID = strings.TrimSpace(after.ReturnValue)
			if lastID != originalViewID {
				switched = true
				break
			}
			time.Sleep(500 * time.Millisecond)
		}
		if !switched {
			t.Fatalf("RequestViewChange reported success but the active view is still id %s after 20s -- the request was accepted and then dropped, which is worse than a refusal because a script cannot detect it", lastID)
		}
	})
}

// TestStairsEditScopeCannotCommitWhileAConnectorTransactionIsOpen is KEPT now that
// settle-on-request has shipped, and its job has changed: it no longer records a
// dead end, it pins the reason the fix has the shape it does.
//
// What it asserts is still true and still matters -- WITHOUT the connector closing
// the transaction, an edit scope cannot commit, however well Start() and the write
// went. That is exactly the property Connector.WithTransaction's closing edge
// exists to satisfy, so this is the negative half of
// TestStairsAreCreatableWithSettleOnRequest above: remove that closing edge and
// this test still passes while the positive one fails, which is what makes the
// pair diagnostic rather than merely green.
//
// Do NOT relax or delete it on the grounds that stairs now work. Its start-edge
// twin was removed because the primitive made ITS claim false; this one's claim is
// unchanged by the primitive.
//
// #115 states the dead end as "no script-reachable code path satisfies both 'no
// ambient transaction to start the edit scope' and 'a transaction open to write
// to it'." That is not the blocker: both ARE satisfiable today, and the first
// three steps below prove it, using only shipped members. Nothing is open on the
// fixture so Start() succeeds, and a Connector.WithTransaction block -- opened
// INSIDE the scope -- supplies the transaction the runs need.
//
// The blocker is a THIRD condition nobody named: no transaction may be open
// when the edit scope COMMITS. Cancel() refuses identically, so the scope
// cannot even be abandoned, and closing a connector-owned transaction mid-run
// is precisely what no script can do.
//
// Note what Revit's message says versus what is true. It names "a transaction
// or transaction group", but the group is NOT the bar: with the connector's
// transaction committed and its group still open, EditScope.Commit() succeeds
// (verified live during #115's triage, in the state no sanctioned path exposes
// yet). That distinction is what makes #115's fix a callback whose transaction
// CLOSES before the scope commits, rather than merely a way to start the scope.
//
// It pins the COMMIT edge specifically: the scope's Commit attempted from INSIDE
// the block whose transaction is still open.
func TestStairsEditScopeCannotCommitWhileAConnectorTransactionIsOpen(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	probe := runScript(t, c, instanceID, documentID, fixtureLookupPreamble(fixtureTitle)+`
Autodesk.Revit.DB.Level l1 = null, l2 = null;
foreach (Autodesk.Revit.DB.Element e in new Autodesk.Revit.DB.FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Level))) {
  var lv = (Autodesk.Revit.DB.Level)e;
  if (l1 == null) { l1 = lv; } else if (l2 == null && lv.Elevation != l1.Elevation) { l2 = lv; }
}
if (l1 == null || l2 == null) { throw new System.Exception("blank fixture document does not have the two template levels this probe needs"); }

// 1. no transaction on this document (the resting state), so Start() is legal
var scope = new Autodesk.Revit.DB.StairsEditScope(doc, "harness #115 commit-edge probe");
var stairsId = scope.Start(l1.Id, l2.Id);

// 2.-4. inside a block: the run can be written, and the commit hits the wall while the block's
// transaction is still open. (Committing AFTER the block is the working shape, pinned by
// TestStairsAreCreatableWithSettleOnRequest.)
return Connector.WithTransaction(doc, () => {
  var line = Autodesk.Revit.DB.Line.CreateBound(new Autodesk.Revit.DB.XYZ(0, 0, 0), new Autodesk.Revit.DB.XYZ(20, 0, 0));
  var run = Autodesk.Revit.DB.Architecture.StairsRun.CreateStraightRun(doc, stairsId, line, Autodesk.Revit.DB.Architecture.StairsRunJustification.Center);

  string commitOutcome;
  try {
    scope.Commit(new Preproc());
    commitOutcome = "committed";
  } catch (Autodesk.Revit.Exceptions.InvalidOperationException ex) {
    commitOutcome = "refused: " + ex.Message;
  }
  return new { started = stairsId.Value > 0, wroteRun = run != null, commitOutcome };
});

class Preproc : Autodesk.Revit.DB.IFailuresPreprocessor {
  public Autodesk.Revit.DB.FailureProcessingResult PreprocessFailures(Autodesk.Revit.DB.FailuresAccessor a) {
    a.DeleteAllWarnings();
    return Autodesk.Revit.DB.FailureProcessingResult.Continue;
  }
}
`)
	if probe.Status != "success" {
		t.Fatalf("steps 1-3 are expected to SUCCEED on shipped code -- if the run failed before reaching the commit, #115's dead end has moved and this test's premise needs re-deriving; status=%q (%s)", probe.Status, probe.diag())
	}
	for _, want := range []string{`"started":true`, `"wroteRun":true`} {
		if !strings.Contains(probe.ReturnValue, want) {
			t.Errorf("wanted %q -- StairsEditScope.Start() and CreateStraightRun both work today; only the commit does not; (%s)", want, probe.diag())
		}
	}
	if !strings.Contains(probe.ReturnValue, "EditScope cannot be closed") {
		t.Fatalf("EditScope.Commit() was expected to be refused while the connector holds a transaction on the document (issue #115). This is the property Connector.WithTransaction's closing edge exists to satisfy: if a scope now commits with a transaction still open, that closing edge is no longer load-bearing and TestStairsAreCreatableWithSettleOnRequest is passing for a reason other than the one it claims; (%s)", probe.diag())
	}

	// THE PART THAT MAKES THIS A SILENT failure, and the reason both skill.md
	// and caveats.md now tell an agent to check Document.IsInEditMode() rather
	// than trust an edit-scope result: the run above reported status "success"
	// while producing nothing at all. Asserting only the refusal would miss
	// that entirely, and the silence is what cost the interactive session
	// behind #115 the most time.
	after := runScript(t, c, instanceID, documentID, fixtureLookupPreamble(fixtureTitle)+`
return new {
  inEditMode = doc.IsInEditMode(),
  stairs = new Autodesk.Revit.DB.FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Architecture.Stairs)).GetElementCount(),
  runs = new Autodesk.Revit.DB.FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Architecture.StairsRun)).GetElementCount()
};
`)
	if after.Status != "success" {
		t.Fatalf("re-reading the fixture after the failed edit scope: status=%q (%s)", after.Status, after.diag())
	}
	if !strings.Contains(after.ReturnValue, `"inEditMode":false`) {
		t.Errorf("the fixture document was left IN EDIT MODE after the failed commit -- the connector's unwind is expected to exit it (TransactionGroup.RollBack does so even from inside a scope, verified live in #115's triage). A document stuck in edit mode will wedge later cases in this shared session; (%s)", after.diag())
	}
	for _, want := range []string{`"stairs":0`, `"runs":0`} {
		if !strings.Contains(after.ReturnValue, want) {
			t.Errorf("wanted %q -- the failed edit scope must leave NOTHING behind, and a partially-built stair surviving would be worse than the current silent no-op; (%s)", want, after.diag())
		}
	}
}

// TestWithTransactionReturnsTheBodysValue pins the value-returning overload
// added by #146 Phase 0 (H4): `var id = Connector.WithTransaction(doc, () =>
// Level.Create(doc, e).Id);` hands the block's result back, so the "create X,
// return its id" shape needs no local hoisted out of the block.
//
// Two things only live Revit can prove. First, OVERLOAD RESOLUTION on a real
// script: an expression-bodied lambda whose body is a non-void call is
// applicable to BOTH the Action and the Func<T> form, and C# is supposed to
// prefer the value-returning delegate -- `created` being non-null below is
// that rule holding against the real compiler and the real API. Second, that
// the generic form rides the SAME commit/unwind path as the Action form: its
// committed level survives, and a body that throws leaves nothing behind
// even when the script catches (the WEDGE/silent-commit pair tier 1 pins on a
// fake, asserted here on a real document).
func TestWithTransactionReturnsTheBodysValue(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
// Expression-bodied, non-void: must bind to the Func<T> overload.
var created = Connector.WithTransaction(doc, () => Autodesk.Revit.DB.Level.Create(doc, 66.6).Id);

// The throwing shape, caught by the script: the generic form must unwind its
// own block exactly as the Action form does.
bool caught = false;
try {
  Connector.WithTransaction<int>(doc, () => {
    Autodesk.Revit.DB.Level.Create(doc, 67.6);
    throw new System.InvalidOperationException("harness: deliberate throw inside WithTransaction<T>");
  });
} catch (System.InvalidOperationException) {
  caught = true;
}

int atReturned = 0, atThrown = 0;
foreach (var e in new Autodesk.Revit.DB.FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Level))) {
  var lvl = e as Autodesk.Revit.DB.Level;
  if (lvl == null) continue;
  if (System.Math.Abs(lvl.Elevation - 66.6) < 0.01) atReturned++;
  if (System.Math.Abs(lvl.Elevation - 67.6) < 0.01) atThrown++;
}
var found = created == null ? null : doc.GetElement(created) as Autodesk.Revit.DB.Level;
return new {
  hasId = created != null && created != Autodesk.Revit.DB.ElementId.InvalidElementId,
  foundByReturnedId = found != null && System.Math.Abs(found.Elevation - 66.6) < 0.01,
  caught, atReturned, atThrown,
};
`)
	if out.Status != "success" {
		t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
	}
	for _, want := range []string{`"hasId":true`, `"foundByReturnedId":true`, `"caught":true`, `"atReturned":1`, `"atThrown":0`} {
		if !strings.Contains(out.ReturnValue, want) {
			t.Errorf("wanted %s in the result -- hasId/foundByReturnedId prove the Func<T> overload was chosen and returned the real id; atThrown:0 proves the generic form unwinds a thrown block like the Action form; (%s)", want, out.diag())
		}
	}
}

// TestTargetMustNotBeModifiableIsMappedToItsOwnCode is the live half of the
// `script-target-must-not-be-modifiable` mapping (#146 Phase 0, H10's inverse).
// Tier 1 pins the code and remedy against the MESSAGE strings; only here can we
// prove those strings are Revit's actual wording -- the match fails OPEN if
// they are not (the run would report plain script-execution-failed), which is
// exactly the failure mode a fake cannot see. Each subtest lets the real
// exception propagate out of the script, unlike the sibling RequestViewChange
// test that catches it to inspect the message.
func TestTargetMustNotBeModifiableIsMappedToItsOwnCode(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	assertMapped := func(t *testing.T, rej rejectedScript, api string) {
		t.Helper()
		if rej.Error.Code != "script-target-must-not-be-modifiable" {
			t.Fatalf("%s against a modifiable target must map to `script-target-must-not-be-modifiable`, got %q -- if this is `script-execution-failed`, Revit's message no longer contains a phrase RequestDispatcher.IsTargetMustNotBeModifiable matches and the mapping is failing open; message: %s", api, rej.Error.Code, rej.Error.Message)
		}
		if joined := strings.Join(rej.Error.Remedy, " "); !strings.Contains(joined, "OUTSIDE your Connector.WithTransaction block") {
			t.Errorf("the remedy must say to move the call outside the WithTransaction block (documents are not modifiable by default since #146 Phase 3); got: %q", joined)
		}
	}

	t.Run("LoadFamilyIntoAModifiableTargetDocument", func(t *testing.T) {
		probe := runScript(t, c, instanceID, documentID, `
var app = UIApplication.Application;
foreach (var f in System.IO.Directory.EnumerateFiles(app.FamilyTemplatePath, "Generic Model.rft", System.IO.SearchOption.AllDirectories)) { return f; }
return "";
`)
		template := strings.TrimSpace(strings.Trim(probe.ReturnValue, `"`))
		if template == "" {
			t.Skip("no Generic Model.rft under Application.FamilyTemplatePath; cannot build a family to load")
		}

		// Since #146 Phase 3 no document is modifiable by default (a group, no transaction), so
		// LoadFamily works at top level; to hit Revit's "must not be modifiable" the call is made
		// INSIDE a WithTransaction block on the TARGET. (A block on the SOURCE does not trigger it:
		// live on Revit 2025 under Phase 3, LoadFamily from a modifiable family document into a
		// non-modifiable target succeeded -- the pre-Phase-3 "source must not be modifiable" finding
		// was the always-open target being blamed on the source.) The created document outlives the
		// rejected run; the marker lets us close it.
		rej := runRejectedScript(t, c, instanceID, documentID, fmt.Sprintf(`
var fam = Connector.CreateFamilyDocument(%s);
System.Console.WriteLine("cleanup-title=" + fam.Title + ";");
var loaded = Connector.WithTransaction(Document, () => fam.LoadFamily(Document));
return loaded == null ? "not-loaded" : "loaded";
`, strconv.Quote(template)))
		for _, title := range cleanupTitles(rej.Output) {
			title := title
			t.Cleanup(func() { closeDocumentByTitle(t, c, instanceID, documentID, title, "") })
		}
		assertMapped(t, rej, "Document.LoadFamily")
	})

	t.Run("RequestViewChangeAtTheActiveDocument", func(t *testing.T) {
		probe := runScript(t, c, instanceID, documentID, `
foreach (Autodesk.Revit.DB.Element e in new Autodesk.Revit.DB.FilteredElementCollector(Document).OfClass(typeof(Autodesk.Revit.DB.ViewPlan))) {
  var v = (Autodesk.Revit.DB.View)e;
  if (!v.IsTemplate && v.Id != UIDocument.ActiveView.Id) { return "has-other-view"; }
}
return "no-other-view";
`)
		if !strings.Contains(probe.ReturnValue, "has-other-view") {
			t.Skip("active document has fewer than two non-template plan views; nothing to switch between")
		}
		rej := runRejectedScript(t, c, instanceID, documentID, `
Autodesk.Revit.DB.View target = null;
foreach (Autodesk.Revit.DB.Element e in new Autodesk.Revit.DB.FilteredElementCollector(Document).OfClass(typeof(Autodesk.Revit.DB.ViewPlan))) {
  var v = (Autodesk.Revit.DB.View)e;
  if (!v.IsTemplate && v.Id != UIDocument.ActiveView.Id) { target = v; break; }
}
Connector.WithTransaction(Document, () => { UIDocument.RequestViewChange(target); });
return "accepted";
`)
		assertMapped(t, rej, "UIDocument.RequestViewChange")
	})
}

// TestDocumentsAreNotModifiableUntilABlockOpens pins the #146 Phase 3 default
// live: the connector opens a GROUP for the routed document and no transaction,
// so it is readable but not modifiable until a Connector.WithTransaction block
// opens one, and not again after the block closes. This is the resting state
// every other test in this file now writes from; a regression here (a
// transaction open by default) would make every LoadFamily / EditScope /
// RequestViewChange call fail again with "must not be modifiable".
func TestDocumentsAreNotModifiableUntilABlockOpens(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
bool activeBefore = Document.IsModifiable;
bool fixtureBefore = doc.IsModifiable;
bool insideActive = Connector.WithTransaction(Document, () => Document.IsModifiable);
bool insideFixture = Connector.WithTransaction(doc, () => doc.IsModifiable);
bool activeAfter = Document.IsModifiable;
bool fixtureAfter = doc.IsModifiable;
return new { activeBefore, fixtureBefore, insideActive, insideFixture, activeAfter, fixtureAfter };
`)
	if out.Status != "success" {
		t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
	}
	for _, want := range []string{`"activeBefore":false`, `"fixtureBefore":false`, `"insideActive":true`, `"insideFixture":true`, `"activeAfter":false`, `"fixtureAfter":false`} {
		if !strings.Contains(out.ReturnValue, want) {
			t.Errorf("wanted %s -- group-always, transaction-on-write: modifiable only inside a WithTransaction block; (%s)", want, out.diag())
		}
	}
}

// TestSubTransactionIsASavepointInsideTheConnectorsTransaction is the live
// half of #146 Phase 1 (#143): a native Autodesk.Revit.DB.SubTransaction is
// PERMITTED, and it behaves as the intra-run savepoint the design leans on.
// Tier 1 pins only that the denylist no longer refuses the construction;
// everything below is a fact about Revit that a fake cannot supply.
func TestSubTransactionIsASavepointInsideTheConnectorsTransaction(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	t.Run("RollBackDiscardsOnlyTheSubTransactionsSlice", func(t *testing.T) {
		// Inside a WithTransaction block: a level created before the savepoint and
		// one created after it both survive; the one created INSIDE the rolled-back
		// sub-transaction is gone.
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+withTx(`
Autodesk.Revit.DB.Level.Create(doc, 71.1);
using (var st = new Autodesk.Revit.DB.SubTransaction(doc)) {
  st.Start();
  Autodesk.Revit.DB.Level.Create(doc, 72.2);
  st.RollBack();
}
using (var st = new Autodesk.Revit.DB.SubTransaction(doc)) {
  st.Start();
  Autodesk.Revit.DB.Level.Create(doc, 73.3);
  st.Commit();
}
Autodesk.Revit.DB.Level.Create(doc, 74.4);
int before = 0, rolledBack = 0, committed = 0, after = 0;
foreach (var e in new Autodesk.Revit.DB.FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Level))) {
  var lvl = e as Autodesk.Revit.DB.Level;
  if (lvl == null) continue;
  if (System.Math.Abs(lvl.Elevation - 71.1) < 0.01) before++;
  if (System.Math.Abs(lvl.Elevation - 72.2) < 0.01) rolledBack++;
  if (System.Math.Abs(lvl.Elevation - 73.3) < 0.01) committed++;
  if (System.Math.Abs(lvl.Elevation - 74.4) < 0.01) after++;
}
return new { before, rolledBack, committed, after };
`))
		if out.Status != "success" {
			t.Fatalf("expected status=success -- if the code is script-api-denied, SubTransaction is back in ScriptApiDenylist's constructed-types table; got %q (%s)", out.Status, out.diag())
		}
		for _, want := range []string{`"before":1`, `"rolledBack":0`, `"committed":1`, `"after":1`} {
			if !strings.Contains(out.ReturnValue, want) {
				t.Errorf("wanted %s -- a SubTransaction must be a savepoint: RollBack discards exactly its own slice, Commit keeps it, and writes outside it are untouched either way; (%s)", want, out.diag())
			}
		}
	})

	t.Run("StartOutsideAnyTransactionIsMappedToItsOwnCode", func(t *testing.T) {
		// Outside any WithTransaction block -- the RESTING state since #146 Phase 3 -- the document has
		// no open transaction, which is the one state a SubTransaction cannot start in. Revit's own
		// message names neither the connector nor the fix; the mapping does. Lets the exception
		// PROPAGATE so the code on the wire is what is asserted.
		rej := runRejectedScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
using (var st = new Autodesk.Revit.DB.SubTransaction(doc)) {
  st.Start();
  st.RollBack();
}
return "started";
`)
		if rej.Error.Code != "script-subtransaction-needs-transaction" {
			t.Fatalf("SubTransaction.Start with no open transaction must map to `script-subtransaction-needs-transaction`, got %q -- if this is `script-execution-failed`, Revit's message no longer contains a phrase RequestDispatcher.IsSubTransactionOutsideTransaction matches and the mapping is failing open; message: %s", rej.Error.Code, rej.Error.Message)
		}
		if joined := strings.Join(rej.Error.Remedy, " "); !strings.Contains(joined, "Connector.WithTransaction") {
			t.Errorf("the remedy must name Connector.WithTransaction as the way to have a transaction open; got: %q", joined)
		}
	})

	// DELIBERATELY NO SUBTEST for a SubTransaction LEFT OPEN at the end of a WithTransaction block
	// (#146 H8). It was run live once, on Revit 2025, and the evidence is recorded here instead:
	//
	//   {"blockError":"","secondBlockRan":true,"forgotten":1}   -- and then Revit CRASHED
	//   ("A software problem has caused Revit 2025.4 to close unexpectedly") on the fixture
	//   document's Close(false) in this test's cleanup, ~45s later.
	//
	// So the connector's Commit met the open sub-transaction and Revit reported nothing: no
	// exception, the slice was kept, the next block ran. The document was then unstable -- the likely
	// mechanism is the script's undisposed SubTransaction wrapper being finalized against a
	// transaction that had already ended. ScriptApiDenylist therefore refuses any SubTransaction
	// construction that is not a `using` resource (compile-time; pinned at tier 1 and in
	// TestDenylistRejectsOwnTransaction), and the `using`-only path -- Dispose doing the rollback with
	// no explicit Commit/RollBack -- is pinned live in the subtest below. A live test that crashes the
	// shared Revit session cannot stay in the suite.

	t.Run("DisposeAloneRollsBackAnUnfinishedSubTransaction", func(t *testing.T) {
		// The safety net the `using` requirement rests on, verified rather than assumed: a
		// SubTransaction that is Started and then only DISPOSED (no Commit, no RollBack) must roll its
		// slice back, leave the enclosing block committable, and leave the document closable -- the
		// fixture's cleanup Close is the same call that crashed Revit in the bare-construction probe.
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+withTx(`
using (var st = new Autodesk.Revit.DB.SubTransaction(doc)) {
  st.Start();
  Autodesk.Revit.DB.Level.Create(doc, 76.6);
  // no Commit, no RollBack: Dispose is all that ends it
}
Autodesk.Revit.DB.Level.Create(doc, 77.7);
int disposedOnly = 0, after = 0;
foreach (var e in new Autodesk.Revit.DB.FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Level))) {
  var lvl = e as Autodesk.Revit.DB.Level;
  if (lvl == null) continue;
  if (System.Math.Abs(lvl.Elevation - 76.6) < 0.01) disposedOnly++;
  if (System.Math.Abs(lvl.Elevation - 77.7) < 0.01) after++;
}
return new { disposedOnly, after };
`))
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		if !strings.Contains(out.ReturnValue, `"disposedOnly":0`) || !strings.Contains(out.ReturnValue, `"after":1`) {
			t.Errorf("Dispose alone must roll back the unfinished sub-transaction's slice and leave the enclosing transaction usable -- this is the property the `using` requirement in ScriptApiDenylist rests on; (%s)", out.diag())
		}
	})
}

// TestMutationReportDescribesWhatTheRunChanged is the live half of the #146
// Phase 2 mutation report. Tier 1 pins the tracker's set algebra over
// hand-built events; only here can we learn what Revit's DocumentChanged
// actually raises -- one event per commit, category names, the shape of a
// group rollback -- and that the field reaches the caller with the names the
// broker reads.
func TestMutationReportDescribesWhatTheRunChanged(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	t.Run("ReadOnlyRunCarriesNoReport", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, `return new Autodesk.Revit.DB.FilteredElementCollector(Document).OfClass(typeof(Autodesk.Revit.DB.Level)).GetElementCount();`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		if out.Mutations != nil {
			t.Errorf("a read-only run must carry no mutations field, got %+v", *out.Mutations)
		}
	})

	t.Run("NetCountsAndCategoriesAcrossOneRun", func(t *testing.T) {
		// Two levels created; one of them then edited (still "created", once); a third created and
		// deleted (nets to nothing); one PRE-EXISTING level edited (modified). Expected: created 2,
		// deleted 0, modified >= 1 (Revit may mark dependents modified on regeneration, so >=), and
		// by_category.Levels.created == 2.
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+withTx(`
var a = Autodesk.Revit.DB.Level.Create(doc, 80.1);
var b = Autodesk.Revit.DB.Level.Create(doc, 81.1);
b.Elevation = 81.2;
var gone = Autodesk.Revit.DB.Level.Create(doc, 82.1);
doc.Delete(gone.Id);
Autodesk.Revit.DB.Level existing = null;
foreach (var e in new Autodesk.Revit.DB.FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Level))) {
  var lvl = e as Autodesk.Revit.DB.Level;
  if (lvl != null && lvl.Id != a.Id && lvl.Id != b.Id) { existing = lvl; break; }
}
if (existing == null) { return "no-preexisting-level"; }
existing.Name = existing.Name + " (renamed)";
return "wrote";
`))
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		if strings.Contains(out.ReturnValue, "no-preexisting-level") {
			t.Skip("fixture template has no pre-existing level to modify")
		}
		if out.Mutations == nil {
			t.Fatalf("a run that wrote must carry a mutations field; result: %s", out.diag())
		}
		m := *out.Mutations
		if m.Created != 2 {
			t.Errorf("created = %d, want 2 (a created-then-deleted element must net to nothing; a created-then-edited one counts once): %+v", m.Created, m)
		}
		if m.Deleted != 0 {
			t.Errorf("deleted = %d, want 0 -- the deleted level was created in this same run: %+v", m.Deleted, m)
		}
		if m.Modified < 1 || m.ByCategory["Levels"].Modified < 1 {
			t.Errorf("modified = %d (Levels.modified = %d), want >= 1 with the renamed pre-existing level counted under its category -- regeneration noise alone must not be what satisfies this: %+v", m.Modified, m.ByCategory["Levels"].Modified, m)
		}
		if got := m.ByCategory["Levels"].Created; got != 2 {
			t.Errorf("by_category.Levels.created = %d, want 2 -- category names must be resolved at event time: %+v", got, m)
		}
		if m.Truncated {
			t.Errorf("a handful of elements must not hit the category cap: %+v", m)
		}
	})

	t.Run("ACaughtThrowInsideWithTransactionContributesNothing", func(t *testing.T) {
		// The rolled-back block's level must not appear: either Revit raises no event for a
		// transaction that never committed, or it raises the reverse -- both net to zero.
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
try {
  Connector.WithTransaction(doc, () => {
    Autodesk.Revit.DB.Level.Create(doc, 83.1);
    throw new System.InvalidOperationException("harness: deliberate");
  });
} catch (System.InvalidOperationException) { }
Connector.WithTransaction(doc, () => { Autodesk.Revit.DB.Level.Create(doc, 84.1); });
return "done";
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		if out.Mutations == nil || out.Mutations.Created != 1 {
			t.Errorf("want exactly the surviving block's level (created 1), got %+v", out.Mutations)
		}
	})

	t.Run("ASettleDiscardedDocumentIsLeftOut", func(t *testing.T) {
		// #146 verification item 4: whatever DocumentChanged raises for TransactionGroup.RollBack, a
		// document settled with keep:false must contribute nothing. Its writes are gone.
		scratchTitle := createBlankFixtureDocument(t, c, instanceID, documentID)
		out := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID,
			fixtureWritePreamble(scratchTitle)+withTx(`
Autodesk.Revit.DB.Level.Create(doc, 85.1);
Autodesk.Revit.DB.Level.Create(doc, 86.1);
Connector.Settle(doc, false);
return "discarded";
`), map[string]any{"confirm_lifecycle_actions": true}))
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		if out.Mutations != nil {
			t.Errorf("a run whose only writes were discarded by Settle(keep:false) must carry no mutations, got %+v", *out.Mutations)
		}
	})
}

// TestUndoLabelIsAcceptedByRevit is the live half of #146 Phase 2b. Revit's
// Undo history is not API-inspectable, so what a person SEES ("MCP: create
// L1 walls" instead of "MCP Bridge Script") is a visual check, to be recorded
// on the epic by whoever performs it. What CAN be pinned live is that both
// label paths run to completion against real Revit AND that the derived
// path's TransactionGroup.SetName -- called between the ambient commit and
// Assimilate, a moment whose legality only Revit can confirm -- was accepted:
// a refused rename is reported as an `undo-label-not-applied` notice, so its
// absence here is the assertion (the first version of this test could not
// tell a refused rename from an applied one; independent review).
func TestUndoLabelIsAcceptedByRevit(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	t.Run("AgentLabel", func(t *testing.T) {
		out := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID,
			fixtureWritePreamble(fixtureTitle)+withTx(`
Autodesk.Revit.DB.Level.Create(doc, 90.1);
return "labelled";
`), map[string]any{"label": "harness: label\nwith newline and a very long tail " + strings.Repeat("x", 200)}))
		if out.Status != "success" {
			t.Fatalf("a labelled run must succeed -- if Revit rejected the sanitised name as a transaction name, this is where it shows; got %q (%s)", out.Status, out.diag())
		}
		if out.Mutations == nil || out.Mutations.Created != 1 {
			t.Errorf("the labelled run's write must land like any other: %+v", out.Mutations)
		}
	})

	t.Run("DerivedLabel", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+withTx(`
Autodesk.Revit.DB.Level.Create(doc, 91.1);
Autodesk.Revit.DB.Level.Create(doc, 92.1);
return "derived";
`))
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		if out.Mutations == nil || out.Mutations.Created != 2 {
			t.Errorf("want created:2 so the derived label would read 'MCP: 2 Levels created': %+v", out.Mutations)
		}
		for _, n := range out.Notices {
			if n.Code == "undo-label-not-applied" {
				t.Errorf("Revit refused TransactionGroup.SetName between commit and Assimilate -- the derived-label tier is a no-op live: %s", n.Message)
			}
		}
	})
}

// TestUndoAndRedoToolsRevertAndRestoreTheLastRun is the live half of #146
// Phase 2c. Revit's undo stack is per document and the tools act on the
// ACTIVE document's stack, so -- unlike every other case in this file -- this
// one writes to the routed (active) document on purpose and then uses the
// tools themselves to leave it as it found it: create -> undo -> redo -> undo.
// What only Revit can confirm: PostCommand(Undo) posted from inside an
// ExternalEvent runs after that event returns and raises DocumentChanged with
// the reverted transaction's NAME -- which is what lets the connector tell an
// agent whether it undid its own work or a person's.
func TestUndoAndRedoToolsRevertAndRestoreTheLastRun(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	callUndoRedo := func(t *testing.T, tool string, args map[string]any) json.RawMessage {
		t.Helper()
		args["instance_id"] = instanceID
		raw, err := c.CallTool(tool, args, 40*time.Second)
		if err != nil {
			t.Fatalf("%s: %v", tool, err)
		}
		return raw
	}
	levelCount := func(t *testing.T) string {
		t.Helper()
		out := runScript(t, c, instanceID, documentID, `
int n = 0;
foreach (var e in new Autodesk.Revit.DB.FilteredElementCollector(Document).OfClass(typeof(Autodesk.Revit.DB.Level))) {
  var lvl = e as Autodesk.Revit.DB.Level;
  if (lvl != null && System.Math.Abs(lvl.Elevation - 93.3) < 0.01) n++;
}
return n;
`)
		return strings.TrimSpace(out.ReturnValue)
	}

	t.Run("WithoutConfirmIsRefused", func(t *testing.T) {
		rej := rejectionOf(t, callUndoRedo(t, "undo", map[string]any{}))
		if rej.Error.Code != "undo-confirmation-required" {
			t.Fatalf("undo without confirm must be refused with undo-confirmation-required, got %q: %s", rej.Error.Code, rej.Error.Message)
		}
	})

	// The labelled write the tools will act on. Routed at the ACTIVE document.
	created := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID,
		`Connector.WithTransaction(Document, () => { Autodesk.Revit.DB.Level.Create(Document, 93.3); }); return "created";`,
		map[string]any{"label": "harness undo probe"}))
	if created.Status != "success" || created.Mutations == nil || created.Mutations.Created != 1 {
		t.Fatalf("the probe write must succeed with created:1; status=%q mutations=%+v (%s)", created.Status, created.Mutations, created.diag())
	}
	// Whatever happens below, do not leave the probe level in the person's model.
	t.Cleanup(func() {
		if levelCount(t) != "0" {
			runScript(t, c, instanceID, documentID, `
Connector.WithTransaction(Document, () => {
  foreach (var e in new Autodesk.Revit.DB.FilteredElementCollector(Document).OfClass(typeof(Autodesk.Revit.DB.Level)).ToElements()) {
    var lvl = e as Autodesk.Revit.DB.Level;
    if (lvl != null && System.Math.Abs(lvl.Elevation - 93.3) < 0.01) Document.Delete(lvl.Id);
  }
});
return "cleaned";`)
		}
	})

	t.Run("UndoRevertsTheLabelledRun", func(t *testing.T) {
		out := decodeToolResult[executeScriptOut](t, callUndoRedo(t, "undo", map[string]any{"confirm": true}))
		if out.Status != "success" {
			t.Fatalf("undo: status=%q (%s)", out.Status, out.diag())
		}
		if out.Mutations == nil || out.Mutations.Deleted != 1 {
			t.Errorf("the undo's reverted delta must show the level removed (deleted:1): %+v", out.Mutations)
		}
		var found bool
		for _, n := range out.Notices {
			if n.Code == "undo-reverted-connector-work" && strings.Contains(n.Message, "MCP: harness undo probe") {
				found = true
			}
			if n.Code == "undo-reverted-other-work" {
				t.Errorf("the undo reverted something other than the connector's labelled run: %s", n.Message)
			}
		}
		if !found {
			t.Errorf("expected an undo-reverted-connector-work notice naming 'MCP: harness undo probe'; notices=%+v", out.Notices)
		}
		if got := levelCount(t); got != "0" {
			t.Errorf("after undo the probe level must be gone, found %s", got)
		}
	})

	t.Run("RedoRestoresIt", func(t *testing.T) {
		out := decodeToolResult[executeScriptOut](t, callUndoRedo(t, "redo", map[string]any{"confirm": true}))
		if out.Status != "success" {
			t.Fatalf("redo: status=%q (%s)", out.Status, out.diag())
		}
		if out.Mutations == nil || out.Mutations.Created != 1 {
			t.Errorf("the redo's delta must show the level back (created:1): %+v", out.Mutations)
		}
		if got := levelCount(t); got != "1" {
			t.Errorf("after redo the probe level must be back, found %s", got)
		}
	})

	t.Run("UndoAgainLeavesTheModelAsFound", func(t *testing.T) {
		out := decodeToolResult[executeScriptOut](t, callUndoRedo(t, "undo", map[string]any{"confirm": true}))
		if out.Status != "success" {
			t.Fatalf("second undo: status=%q (%s)", out.Status, out.diag())
		}
		if got := levelCount(t); got != "0" {
			t.Errorf("after the second undo the probe level must be gone, found %s", got)
		}
	})
}
