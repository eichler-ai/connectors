//go:build harness

package harness_test

import (
	"fmt"
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

// TestStairsEditScopeBlockedByAmbientTransaction pins the structural gap
// tracked as issue #115: StairsEditScope.Start() refuses to run while a
// managed transaction is open on its target document. Here that transaction
// is the one Connector.OpenForWriting(doc) opens on the fixture document
// (fixtureWritePreamble) -- the same class of restriction the ambient
// per-call transaction on the routed document itself would produce, but not
// literally the same transaction, so this pins "a managed transaction on the
// target document blocks Start()" rather than "the ambient dispatcher
// transaction specifically blocks it". There is currently no script-reachable
// way around this in either case (deferring past the script's own execution
// via UIApplication.Idling gets Start() to succeed, but
// StairsRun.CreateStraightRun() immediately fails with
// ModificationOutsideTransactionException, and opening a Transaction to
// cover it is unconditionally denylisted -- see
// TestDenylistRejectsOwnTransaction). This pins the first half of that dead
// end, live, so it flips to a clear regression signal the moment #115 ships
// a fix -- at which point this test should be REPLACED with a positive
// assertion using whatever primitive #115 adds, not merely relaxed. Still
// open as of this run (unlike #113/#114/#116/#117/#118, all fixed already).
//
// StairsEditScope's namespace is Autodesk.Revit.DB, not
// Autodesk.Revit.DB.Architecture (unlike Stairs/StairsRun/StairsType) --
// confirmed live via describe_function against the real Revit 2027 API
// during the interactive session this test is drawn from, and confirmed
// again by this test compiling and running to completion rather than
// failing with a CS0234 "type or namespace not found".
func TestStairsEditScopeBlockedByAmbientTransaction(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
var l1 = Autodesk.Revit.DB.Level.Create(doc, 6000.0);
var l2 = Autodesk.Revit.DB.Level.Create(doc, 6010.0);
try {
  var scope = new Autodesk.Revit.DB.StairsEditScope(doc, "harness probe");
  scope.Start(l1.Id, l2.Id);
  return "started";
} catch (Autodesk.Revit.Exceptions.InvalidOperationException ex) {
  return "refused: " + ex.Message;
}
`)
	if out.Status != "success" {
		t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
	}
	if !strings.Contains(out.ReturnValue, "refused:") {
		t.Fatalf("StairsEditScope.Start() was expected to be refused while a managed transaction is open on its target document (issue #115) -- if this now succeeds, stairs creation may finally be reachable and this test should be replaced with a real stairs-creation assertion; (%s)", out.diag())
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
			fmt.Sprintf(buildRoom, "10.0", ""))
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
			fmt.Sprintf(buildRoom, "6200.0", setHeight))
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

	out := runScript(t, c, instanceID, documentID, "using System.Linq;\n"+fixtureWritePreamble(fixtureTitle)+`
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
if (tbType == null) { return "no-titleblock-loaded"; }
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
`)
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

	t.Run("TheRoutedDocumentIsModifiable", func(t *testing.T) {
		out := runScript(t, c, instanceID, fixtureDocID,
			`return Document.IsModifiable ? "modifiable" : "not-modifiable";`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		// "not-modifiable" contains "modifiable", so match on the negative spelling.
		if strings.Contains(out.ReturnValue, "not-modifiable") {
			t.Fatalf("the routed document must be modifiable for the whole run -- if it is not, the ambient managed transaction is no longer being opened and most of this suite's write cases are meaningless; (%s)", out.diag())
		}
	})

	t.Run("TheSameDocumentReachedByTitleFromElsewhereIsNot", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureLookupPreamble(fixtureTitle)+
			`return doc.IsModifiable ? "modifiable" : "not-modifiable";`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		if !strings.Contains(out.ReturnValue, "not-modifiable") {
			t.Fatalf("a document reached by Title from a run routed ELSEWHERE must not be modifiable -- that is the whole routing recipe; (%s)", out.diag())
		}
	})

	t.Run("RequestViewChangeRefusedAtTheActiveDocumentAndSucceedsRoutedAway", func(t *testing.T) {
		// Routed AT the active document: UIDocument is non-null and its own
		// document is modifiable, so Revit refuses. Nothing changes on screen,
		// so this half needs no cleanup.
		refused := runScript(t, c, instanceID, documentID, `
Autodesk.Revit.DB.View target = null;
foreach (Autodesk.Revit.DB.Element e in new Autodesk.Revit.DB.FilteredElementCollector(Document).OfClass(typeof(Autodesk.Revit.DB.ViewPlan))) {
  var v = (Autodesk.Revit.DB.View)e;
  if (!v.IsTemplate && v.Id != UIDocument.ActiveView.Id) { target = v; break; }
}
if (target == null) { return "no-other-view"; }
try {
  UIDocument.RequestViewChange(target);
  return "accepted";
} catch (Autodesk.Revit.Exceptions.InvalidOperationException ex) {
  return "refused: " + ex.Message;
}
`)
		if refused.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", refused.Status, refused.diag())
		}
		if strings.Contains(refused.ReturnValue, "no-other-view") {
			t.Skip("active document has fewer than two non-template plan views; nothing to switch between")
		}
		if !strings.Contains(refused.ReturnValue, "refused:") {
			t.Fatalf("RequestViewChange was expected to be REFUSED from a call routed at the active document, whose managed transaction makes it modifiable -- if this now succeeds, the recipe #131 documents is no longer needed and both skill.md and caveats.md should be corrected; (%s)", refused.diag())
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

// TestStairsEditScopeCannotCommitWhileAConnectorTransactionIsOpen pins the part
// of issue #115 that is a REAL capability gap, and corrects where that gap
// actually sits.
//
// #115 states the dead end as "no script-reachable code path satisfies both 'no
// ambient transaction to start the edit scope' and 'a transaction open to write
// to it'." That is not the blocker: both ARE satisfiable today, and the first
// three steps below prove it, using only shipped members. Routing the call away
// from the fixture leaves it unmanaged so Start() succeeds, and
// Connector.OpenForWriting -- called INSIDE the scope -- supplies the
// transaction the runs need.
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
// This test is a companion to TestStairsEditScopeBlockedByAmbientTransaction
// above, not a replacement: that one pins the START edge (a managed transaction
// opened BEFORE Start()), this one pins the COMMIT edge (a managed transaction
// opened AFTER it). Both must be replaced with a positive stairs-creation
// assertion when #115's second PR ships the primitive -- relaxing either one
// instead would leave the suite claiming stairs are unreachable after they are.
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

// 1. no managed transaction on this document, because the call is routed elsewhere
var scope = new Autodesk.Revit.DB.StairsEditScope(doc, "harness #115 commit-edge probe");
var stairsId = scope.Start(l1.Id, l2.Id);

// 2. the only shipped way to get a transaction -- asked for INSIDE the scope
Connector.OpenForWriting(doc);

// 3. and it really does let the run be written
var line = Autodesk.Revit.DB.Line.CreateBound(new Autodesk.Revit.DB.XYZ(0, 0, 0), new Autodesk.Revit.DB.XYZ(20, 0, 0));
var run = Autodesk.Revit.DB.Architecture.StairsRun.CreateStraightRun(doc, stairsId, line, Autodesk.Revit.DB.Architecture.StairsRunJustification.Center);

// 4. and here is the wall
string commitOutcome;
try {
  scope.Commit(new Preproc());
  commitOutcome = "committed";
} catch (Autodesk.Revit.Exceptions.InvalidOperationException ex) {
  commitOutcome = "refused: " + ex.Message;
}
return new { started = stairsId.Value > 0, wroteRun = run != null, commitOutcome };

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
		t.Fatalf("EditScope.Commit() was expected to be refused while the connector holds a transaction on the document (issue #115). If it now COMMITS, stairs are reachable and this test must be replaced with a positive stairs-creation assertion -- along with TestStairsEditScopeBlockedByAmbientTransaction -- not merely relaxed; (%s)", probe.diag())
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
