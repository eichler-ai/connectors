//go:build harness

package harness_test

import (
	"fmt"
	"strings"
	"testing"
	"time"
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
// UPDATE: by the time this file was wired into a live run, issues #114,
// #116, #117 and #118 had already been fixed and merged (see each test's own
// comment for how that changed what it pins). #113's crash and #115's stairs
// gap were still open as of this run.
//
// One live finding is deliberately NOT given a test here: issue #113 (a real
// Revit fatal crash from reassigning a placed sheet's SheetTitleBlockId to a
// symbol from a different Family). Reproducing a crash would take down the
// shared, long-lived Revit session every other suite in this package runs
// against. TestSheetTitleBlockRecreateWorkaroundIsSafe below pins the safe
// alternative that was used to complete the task instead.

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

	// create returns the new document's Title and its tmp- document_id, waiting
	// on list_instances' live snapshot push (issue #30) for the id to appear.
	create := func() (title, tmpDocID string) {
		out := runScript(t, c, instanceID, documentID, `return Connector.CreateProjectDocument().Title;`)
		if out.Status != "success" {
			t.Fatalf("create failed: status=%q %s", out.Status, out.diag())
		}
		title = strings.TrimSpace(out.ReturnValue)
		if title == "" {
			t.Fatalf("created document reported no title; %s", out.diag())
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

	t.Run("ClosingByRoutingAwayAndFindingByTitleWorks", func(t *testing.T) {
		title, _ := create()
		script := fixtureLookupPreamble(title) + `doc.Close(false); return "closed";`
		out := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID, script,
			map[string]any{"confirm_lifecycle_actions": true}))
		if out.Status != "success" || !strings.Contains(out.ReturnValue, "closed") {
			t.Fatalf("closing a created document by routing elsewhere and finding it by Title should succeed; status=%q %s", out.Status, out.diag())
		}
	})

	t.Run("ClosingByRoutingDirectlyAtItFailsForThatCallOnlyNotForever", func(t *testing.T) {
		title, tmpDocID := create()
		// Safety net regardless of what this subtest proves -- never leaves a
		// created document open.
		t.Cleanup(func() { closeDocumentByTitle(t, c, instanceID, documentID, title, "") })

		blocked := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, tmpDocID, `
try {
  Document.Close(false);
  return "closed";
} catch (Autodesk.Revit.Exceptions.InvalidOperationException ex) {
  return "refused: " + ex.Message;
}
`, map[string]any{"confirm_lifecycle_actions": true}))
		if blocked.Status != "success" {
			t.Fatalf("expected status=success, got %q %s", blocked.Status, blocked.diag())
		}
		if !strings.Contains(blocked.ReturnValue, "refused:") {
			t.Fatalf("routing document_id directly at a created document was expected to block Close for that call (a fresh per-call transaction wraps it); if this now succeeds, the footgun described in issue #114 may already be fixed; %s", blocked.diag())
		}

		// THE ASSERTION THAT WOULD HAVE CAUGHT THE ORIGINAL "permanently
		// unclosable" framing being wrong: a later, separately-routed call still
		// closes it cleanly.
		recheck := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID,
			fixtureLookupPreamble(title)+`doc.Close(false); return "closed";`,
			map[string]any{"confirm_lifecycle_actions": true}))
		if recheck.Status != "success" || !strings.Contains(recheck.ReturnValue, "closed") {
			t.Fatalf("a created document that failed to close via direct routing should still close cleanly via a later, separately-routed call -- if this now fails too, issue #114's ORIGINAL 'permanently unclosable' framing would actually be correct after all; status=%q %s", recheck.Status, recheck.diag())
		}
	})
}

// TestStairsEditScopeBlockedByAmbientTransaction pins the structural gap
// tracked as issue #115: StairsEditScope.Start() refuses to run while the
// connector's own managed transaction is open around every execute_script
// call, which is always the case for a synchronous script. There is
// currently no script-reachable way around this (deferring past the
// script's own execution via UIApplication.Idling gets Start() to succeed,
// but StairsRun.CreateStraightRun() immediately fails with
// ModificationOutsideTransactionException, and opening a Transaction to
// cover it is unconditionally denylisted -- see
// TestDenylistRejectsOwnTransaction). This pins the first half of that dead
// end, live, so it flips to a clear regression signal the moment #115 ships
// a fix -- at which point this test should be REPLACED with a positive
// assertion using whatever primitive #115 adds, not merely relaxed. Still
// open as of this run (unlike #114/#116/#117/#118, all fixed already).
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
		t.Fatalf("expected status=success, got %q %s", out.Status, out.diag())
	}
	if !strings.Contains(out.ReturnValue, "refused:") {
		t.Fatalf("StairsEditScope.Start() was expected to be refused while the ambient transaction is open (issue #115) -- if this now succeeds, stairs creation may finally be reachable and this test should be replaced with a real stairs-creation assertion; %s", out.diag())
	}
}

// TestRoomOnScriptCreatedLevelNeedsComputationHeight pins the live-confirmed
// gotcha behind issue #118: a Level created via Level.Create defaults its
// Room Computation Height to 0 -- exactly the level's own elevation, which
// is also exactly where a wall created with that level as its base
// constraint starts. Placing a room at that computation height can then find
// ZERO boundary loops (not merely a wrong area) even though the surrounding
// walls are geometrically closed and correctly flagged room-bounding --
// confirmed against Autodesk's own KB on computation-height-at-the-boundary.
// FIXED as a documentation correction (#118, landed alongside #114): this
// trap and its recipe are now written down in skill.md/caveats.md rather
// than the default behavior changing, so both subtests below assert the
// SAME thing they always would have -- the gap still reproduces on the
// naive path, and the known-good recipe still works. What changed is that
// an agent hitting this now has a documented way out instead of discovering
// it the hard way, as this session did.
//
// NOT RELIABLY REPRODUCIBLE under this test's own construction, live-tested
// the hard way -- worth being honest about rather than guessing at magic
// numbers until one happens to fail. Two different elevations (6100.0 and
// 10.0, the latter matching the original interactive session's exact value)
// both came back properly enclosed (loops = 1) against a document built here
// via createBlankFixtureDocument (Connector.CreateProjectDocument, which uses
// Revit's own DefaultProjectTemplate). The one remaining difference from the
// original repro not accounted for here: that session's document was created
// via the RAW, unmanaged Application.NewProjectDocument(UnitSystem) path with
// NO template at all, not the templated, managed path this fixture helper
// uses -- not re-tested here, since a raw document is read-only and cannot
// host Wall.Create/NewRoom without its own separate (and separately
// footgun-prone) write path. So the subtest below is deliberately a PROBE,
// not a pass/fail gate: it reports what it finds instead of asserting a
// specific outcome, because this specific reproduction has not been pinned
// down precisely enough yet to assert on with confidence. The known-good
// recipe (explicit computation height) is still asserted as a hard PASS
// below, since that half never depended on reproducing the failure first.
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
			t.Fatalf("expected status=success, got %q %s", out.Status, out.diag())
		}
		if strings.Contains(out.ReturnValue, "loops = 0;") {
			t.Logf("reproduced the gap live: default computation height produced an unenclosed room (issue #118); %s", out.diag())
		} else {
			t.Logf("did NOT reproduce the gap against a Connector.CreateProjectDocument-templated fixture (see this test's doc comment for the untested raw-document variable); %s", out.diag())
		}
	})

	t.Run("SettingComputationHeightInsideTheWallBodyWorks", func(t *testing.T) {
		setHeight := `level.get_Parameter(Autodesk.Revit.DB.BuiltInParameter.LEVEL_ROOM_COMPUTATION_HEIGHT).Set(4.0);`
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+
			fmt.Sprintf(buildRoom, "6200.0", setHeight))
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q %s", out.Status, out.diag())
		}
		if strings.Contains(out.ReturnValue, "loops = 0;") {
			t.Fatalf("the known-good recipe (computation height set inside the wall body) failed to produce an enclosed room; %s", out.diag())
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

	created := runScript(t, c, instanceID, documentID, `return Connector.CreateProjectDocument().Title;`)
	if created.Status != "success" {
		t.Fatalf("create failed: status=%q %s", created.Status, created.diag())
	}
	title := strings.TrimSpace(created.ReturnValue)
	t.Cleanup(func() { closeDocumentByTitle(t, c, instanceID, documentID, title, "") })

	var tmpDocID string
	deadline := time.Now().Add(20 * time.Second)
	for time.Now().Before(deadline) && tmpDocID == "" {
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
					if d.Active {
						t.Errorf("created document unexpectedly reported active:true in list_instances -- if CreateProjectDocument now opens a real window, issue #118 may already be addressed; this test should then assert active:true instead of failing")
					}
					tmpDocID = d.DocumentID
				}
			}
		}
		if tmpDocID == "" {
			time.Sleep(500 * time.Millisecond)
		}
	}
	if tmpDocID == "" {
		t.Fatalf("created document %q never appeared in list_instances", title)
	}

	out := runScript(t, c, instanceID, tmpDocID, `return UIDocument == null ? "no-window" : "has-window";`)
	if out.Status != "success" {
		t.Fatalf("expected status=success, got %q %s", out.Status, out.diag())
	}
	if !strings.Contains(out.ReturnValue, "no-window") {
		t.Errorf("expected UIDocument to be null for a document CreateProjectDocument produced -- if this now has a window, issue #118 may already be addressed; this test should then assert has-window instead; %s", out.diag())
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
// SheetTitleBlockId -- works cleanly, which is the pattern that completed
// the interactive session's task after the crash. Still open as of this run
// (unlike #114/#116/#117/#118): #113 landed a documentation correction (the
// title-block id trap, per its own commit) but the crash path itself was not
// guarded in code, so this test's own reasoning for never exercising the
// mutation still applies unchanged.
func TestSheetTitleBlockRecreateWorkaroundIsSafe(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	out := runScript(t, c, instanceID, documentID, "using System.Linq;\n"+fixtureWritePreamble(fixtureTitle)+`
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
return new { sheetCreated = sheet != null, viewportCreated = viewport != null };
`)
	if out.Status != "success" {
		t.Fatalf("expected status=success, got %q %s", out.Status, out.diag())
	}
	if strings.Contains(out.ReturnValue, "no-titleblock-loaded") {
		t.Skip("no title block family loaded in the fixture document's default template on this machine")
	}
	// return_value is real JSON since issue #117 landed -- a serialized
	// anonymous object, not the old "field = True" ToString() rendering.
	for _, want := range []string{`"sheetCreated":true`, `"viewportCreated":true`} {
		if !strings.Contains(out.ReturnValue, want) {
			t.Errorf("wanted %q in return_value; %s", want, out.diag())
		}
	}
}
