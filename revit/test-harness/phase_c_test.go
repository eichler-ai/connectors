//go:build harness

package harness_test

import (
	"strings"
	"testing"
)

// TestPhaseCFloorsGridsSheetsAndText is the third fixture-system bundle (coverage plan §13's
// tutorial-workflow corpus, continuing Phase A's core-CRUD/query and Phase B's annotation/scheduling
// bundles into floors, grids, sheets/view-placement, and text annotation): one blank document created
// ONCE via createBlankFixtureDocument, then each check below as its own INDEPENDENT t.Run subtest,
// each its own execute_script call, against that same document. Subtests that add geometry do so at
// their own level elevation, so a failure in one does not cascade into another and re-running a single
// subtest in isolation behaves the same as running the whole bundle.
//
// Every script below was researched via search_functions/describe_function FIRST (not guessed, then
// checked) and executed live via mcp__revit__execute_script -- including deliberately probing corner
// cases, not just the happy path -- before being committed here. Two real discovery-tool gaps found
// during that research were filed rather than worked around silently: issue #64 (describe_function's
// overload_index didn't match search_functions' own ranking, and member_id alone was rejected --
// FIXED by PR #66, which dropped overload_index from the tool entirely and made member_id alone
// sufficient; the workaround this comment used to describe no longer applies) and issue #65 (still
// open: search_functions("create sheet place view") never surfaces ViewSheet.Create at all, ranking
// the much-less-common CreatePlaceholder first instead -- worked around below by querying the exact
// method name once the right one is known, same as before).
func TestPhaseCFloorsGridsSheetsAndText(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	// CreateFloor: a closed four-segment CurveLoop profile, Floor.Create against the default floor
	// type (Floor.GetDefaultFloorType(doc, isFoundation: false)).
	t.Run("CreateFloor", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
var level = Autodesk.Revit.DB.Level.Create(doc, 700.0);
var floorTypeId = Autodesk.Revit.DB.Floor.GetDefaultFloorType(doc, false);
var loop = new Autodesk.Revit.DB.CurveLoop();
loop.Append(Autodesk.Revit.DB.Line.CreateBound(new Autodesk.Revit.DB.XYZ(0, 0, 0), new Autodesk.Revit.DB.XYZ(20, 0, 0)));
loop.Append(Autodesk.Revit.DB.Line.CreateBound(new Autodesk.Revit.DB.XYZ(20, 0, 0), new Autodesk.Revit.DB.XYZ(20, 20, 0)));
loop.Append(Autodesk.Revit.DB.Line.CreateBound(new Autodesk.Revit.DB.XYZ(20, 20, 0), new Autodesk.Revit.DB.XYZ(0, 20, 0)));
loop.Append(Autodesk.Revit.DB.Line.CreateBound(new Autodesk.Revit.DB.XYZ(0, 20, 0), new Autodesk.Revit.DB.XYZ(0, 0, 0)));
var profile = new System.Collections.Generic.List<Autodesk.Revit.DB.CurveLoop> { loop };
var floor = Autodesk.Revit.DB.Floor.Create(doc, profile, floorTypeId, level.Id);
doc.Regenerate();
return new { floorCreated = floor != null, category = floor.Category == null ? "null" : floor.Category.Name };
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		for _, want := range []string{"\"floorCreated\":true", "\"category\":\"Floors\""} {
			if !strings.Contains(out.ReturnValue, want) {
				t.Errorf("wanted %q in %s", want, out.diag())
			}
		}
	})

	// CreateGrid: two parallel grid lines via Grid.Create(doc, Line), confirming names auto-increment
	// ("1", then "2") -- the standard structural-grid tutorial step.
	//
	// Corner cases probed live, NOT encoded as assertions here (both landed on ordinary, already-
	// correct behavior with nothing this corpus needs to pin): a degenerate zero-length Line throws a
	// clear Revit ArgumentsInconsistentException ("Curve length is too small for Revit's tolerance")
	// at Line.CreateBound itself, before Grid.Create is ever reached; and a grid line exactly
	// coincident with an existing one is silently allowed (no exception, no warning surfaced) --
	// Revit's own permissiveness, not a connector or discovery gap.
	t.Run("CreateGrid", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
var line1 = Autodesk.Revit.DB.Line.CreateBound(new Autodesk.Revit.DB.XYZ(300, -10, 0), new Autodesk.Revit.DB.XYZ(300, 30, 0));
var grid1 = Autodesk.Revit.DB.Grid.Create(doc, line1);
var line2 = Autodesk.Revit.DB.Line.CreateBound(new Autodesk.Revit.DB.XYZ(330, -10, 0), new Autodesk.Revit.DB.XYZ(330, 30, 0));
var grid2 = Autodesk.Revit.DB.Grid.Create(doc, line2);
// Names auto-increment from whatever the document's next-available grid number already is (not
// necessarily "1"/"2" -- this fixture document could in principle already have grids from
// elsewhere), so the comparison that matters -- they got DIFFERENT auto-assigned names, not
// specific literals -- is done here, in-script, rather than parsed back out of two separate
// output fields on the Go side.
return new { grid1Created = grid1 != null, grid2Created = grid2 != null, namesDiffer = grid1.Name != grid2.Name };
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		for _, want := range []string{"\"grid1Created\":true", "\"grid2Created\":true", "\"namesDiffer\":true"} {
			if !strings.Contains(out.ReturnValue, want) {
				t.Errorf("wanted %q in %s", want, out.diag())
			}
		}
	})

	// CreateSheetAndPlaceView: ViewSheet.Create (no title block, ElementId.InvalidElementId) plus
	// Viewport.Create to place a view on it -- the standard "make a sheet, put a view on it" tutorial
	// step.
	//
	// REAL FINDING FROM LIVE TESTING, not a discovery-tool gap: the default project template ships
	// 22 pre-existing sheets, several of which already have views placed on them (e.g. "L1 -
	// Architectural" is already on sheet A101). Viewport.Create throws ArgumentException ("viewId
	// cannot be added to the ViewSheet") for a view that's already placed anywhere -- confirmed via
	// Viewport.CanAddViewToSheet returning false for exactly that view. This subtest therefore
	// creates its OWN fresh ViewPlan via ViewPlan.Create (guaranteed unplaced) rather than reusing an
	// existing one, the same "don't assume a shared fixture's pre-existing content is available for
	// your subtest to claim" lesson CreateRoomAndTagIt (Phase B) and this bundle's own independence
	// convention already rest on.
	//
	// The corner case that motivated finding this in the first place -- placing an ALREADY-placed
	// view on a second sheet -- IS encoded below: CanAddViewToSheet must report false for it.
	t.Run("CreateSheetAndPlaceView", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
var level = Autodesk.Revit.DB.Level.Create(doc, 760.0);
Autodesk.Revit.DB.ElementId vftId = null;
foreach (Autodesk.Revit.DB.ViewFamilyType vft in new Autodesk.Revit.DB.FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.ViewFamilyType))) {
  if (vft.ViewFamily == Autodesk.Revit.DB.ViewFamily.FloorPlan) { vftId = vft.Id; break; }
}
var freshView = Autodesk.Revit.DB.ViewPlan.Create(doc, vftId, level.Id);
doc.Regenerate();

var sheet = Autodesk.Revit.DB.ViewSheet.Create(doc, Autodesk.Revit.DB.ElementId.InvalidElementId);
var canAddFirstTime = Autodesk.Revit.DB.Viewport.CanAddViewToSheet(doc, sheet.Id, freshView.Id);
var viewport = Autodesk.Revit.DB.Viewport.Create(doc, sheet.Id, freshView.Id, new Autodesk.Revit.DB.XYZ(1, 1, 0));

var secondSheet = Autodesk.Revit.DB.ViewSheet.Create(doc, Autodesk.Revit.DB.ElementId.InvalidElementId);
var canAddSecondTime = Autodesk.Revit.DB.Viewport.CanAddViewToSheet(doc, secondSheet.Id, freshView.Id);

return new {
  vftFound = vftId != null,
  sheetCreated = sheet != null,
  canAddFirstTime,
  viewportCreated = viewport != null,
  canAddSecondTime
};
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		for _, want := range []string{"\"vftFound\":true", "\"sheetCreated\":true", "\"canAddFirstTime\":true", "\"viewportCreated\":true"} {
			if !strings.Contains(out.ReturnValue, want) {
				t.Errorf("wanted %q in %s", want, out.diag())
			}
		}
		if !strings.Contains(out.ReturnValue, "\"canAddSecondTime\":false") {
			t.Errorf("a view already placed on one sheet must not be addable to a second one; %s", out.diag())
		}
	})

	// CreateTextNote: TextNote.Create(doc, viewId, position, text, typeId) -- the unwrapped-text
	// overload, against the document's own first available TextNoteType.
	//
	// REAL FINDING FROM LIVE TESTING: TextNote.Text comes back with a trailing "\r" appended to
	// whatever string was passed in, even with no embedded newlines in the input and nothing in
	// Autodesk's own shipped XML doc for Create mentioning it (confirmed via describe_function --
	// this is undocumented Revit behavior, not a discovery-tool gap to file). Asserted via
	// TrimEnd('\r') below rather than exact equality, so this subtest doesn't silently start failing
	// if a future Revit version changes or drops the trailing character.
	t.Run("CreateTextNote", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
Autodesk.Revit.DB.ViewPlan view = null;
foreach (Autodesk.Revit.DB.ViewPlan v in new Autodesk.Revit.DB.FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.ViewPlan))) { if (!v.IsTemplate) { view = v; break; } }
Autodesk.Revit.DB.ElementId typeId = null;
foreach (Autodesk.Revit.DB.TextNoteType tnt in new Autodesk.Revit.DB.FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.TextNoteType))) { typeId = tnt.Id; break; }
var note = Autodesk.Revit.DB.TextNote.Create(doc, view.Id, new Autodesk.Revit.DB.XYZ(0, 0, 0), "Phase C text note", typeId);
return new { viewFound = view != null, typeFound = typeId != null, noteCreated = note != null, text = note.Text.TrimEnd('\r') };
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		for _, want := range []string{"\"viewFound\":true", "\"typeFound\":true", "\"noteCreated\":true", "\"text\":\"Phase C text note\""} {
			if !strings.Contains(out.ReturnValue, want) {
				t.Errorf("wanted %q in %s", want, out.diag())
			}
		}
	})
}
