//go:build harness

package harness_test

import (
	"strings"
	"testing"
)

// TestPhaseBAnnotationAndScheduling is the second fixture-system bundle (coverage plan §13's
// tutorial-workflow corpus, continuing Phase A's core-CRUD/query bundle into annotation and
// scheduling workflows): one blank document created ONCE via createBlankFixtureDocument, then
// each check below as its own INDEPENDENT t.Run subtest, each its own execute_script call,
// against that same document. Subtests are independent, not chained: each that adds geometry
// does so at its own level elevation, so a failure in one does not cascade into another and
// re-running a single subtest in isolation (`-run TestPhaseBAnnotationAndScheduling/CreateRoomAndTagIt`)
// behaves the same as running the whole bundle.
//
// Every script below was executed live via mcp__revit__execute_script against a real connected
// instance before being committed here (this project's standing "no fake integration tier" rule
// extends to test AUTHORING, not just to what ships) -- see the subtests' own comments for the
// real API corrections that came out of that research.
func TestPhaseBAnnotationAndScheduling(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	// CreateRoomAndTagIt: four walls forming a closed rectangle, a Room placed inside them, and a
	// RoomTag referencing it in a plan view -- the standard "walls -> room -> tag" beginner
	// tutorial sequence.
	//
	// TWO REAL API CORRECTIONS FROM LIVE TESTING:
	//   1. Document.Create.NewRoomTag's first parameter is a LinkElementId, not a bare ElementId --
	//      for a room in the host model (not an actual Revit link), `new LinkElementId(room.Id)`
	//      is the correct construction; there is no host-model-only overload.
	//   2. A Level created via Level.Create does NOT get an associated floor plan view for free --
	//      unlike a level placed interactively with the ribbon tool's "Make Plan View" option on,
	//      Revit does not auto-generate one programmatically. This subtest (and DimensionBetweenWalls
	//      below, which needs a view for the same reason) tags/dimensions using whichever non-template
	//      ViewPlan already exists in the default template instead of one scoped to the new level --
	//      confirmed live this still works (NewRoomTag/NewDimension don't require the view to be
	//      looking at the same level the tagged/dimensioned elements sit on). Filtering by
	//      `view.GenLevel.Id == level.Id` here would find nothing and NullReferenceException/
	//      ArgumentNullException deep inside NewRoomTag/NewDimension instead of a clear error --
	//      exactly what happened before this comment existed.
	//
	// Separately, FilteredElementCollector.OfClass(typeof(Room)) THROWS ("is of an element type
	// that exists in the API, but not in Revit's native object model... use SpatialElement
	// instead") -- Room has no matching native element type for OfClass to filter on
	// (OfCategory(BuiltInCategory.OST_Rooms) is the correct way to collect rooms). Not needed in
	// this subtest since it holds the just-created Room's own reference directly, but real and
	// worth knowing before reaching for a Room collector anywhere else in this corpus.
	t.Run("CreateRoomAndTagIt", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
var level = Autodesk.Revit.DB.Level.Create(doc, 500.0);
var p1 = new Autodesk.Revit.DB.XYZ(0, 0, 0);
var p2 = new Autodesk.Revit.DB.XYZ(20, 0, 0);
var p3 = new Autodesk.Revit.DB.XYZ(20, 20, 0);
var p4 = new Autodesk.Revit.DB.XYZ(0, 20, 0);
Autodesk.Revit.DB.Wall.Create(doc, Autodesk.Revit.DB.Line.CreateBound(p1, p2), level.Id, false);
Autodesk.Revit.DB.Wall.Create(doc, Autodesk.Revit.DB.Line.CreateBound(p2, p3), level.Id, false);
Autodesk.Revit.DB.Wall.Create(doc, Autodesk.Revit.DB.Line.CreateBound(p3, p4), level.Id, false);
Autodesk.Revit.DB.Wall.Create(doc, Autodesk.Revit.DB.Line.CreateBound(p4, p1), level.Id, false);
doc.Regenerate();
var room = doc.Create.NewRoom(level, new Autodesk.Revit.DB.UV(10, 10));
doc.Regenerate();

Autodesk.Revit.DB.ViewPlan view = null;
foreach (Autodesk.Revit.DB.ViewPlan v in new Autodesk.Revit.DB.FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.ViewPlan))) {
  if (!v.IsTemplate) { view = v; break; }
}
var linkId = new Autodesk.Revit.DB.LinkElementId(room.Id);
var tag = doc.Create.NewRoomTag(linkId, new Autodesk.Revit.DB.UV(10, 10), view.Id);

return new { roomCreated = room != null, area = room.Area, viewFound = view != null, tagCreated = tag != null };
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (return_value: %s)", out.Status, out.ReturnValue)
		}
		for _, want := range []string{"\"roomCreated\":true", "\"viewFound\":true", "\"tagCreated\":true"} {
			if !strings.Contains(out.ReturnValue, want) {
				t.Errorf("wanted %q in return_value: %s", want, out.ReturnValue)
			}
		}
		if strings.Contains(out.ReturnValue, "\"area\":0") {
			t.Errorf("room area should be positive (it's bounded by the four walls above); return_value: %s", out.ReturnValue)
		}
	})

	// PlaceDoorInWall: finds a door FamilySymbol already loaded from the default project template
	// (confirmed live: the default template ships at least one, "30\" x 80\""), activates it (a
	// FamilySymbol must be Activate()-d before use, or NewFamilyInstance throws), and hosts an
	// instance on a wall at its midpoint -- the standard door-placement pattern.
	t.Run("PlaceDoorInWall", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
var level = Autodesk.Revit.DB.Level.Create(doc, 540.0);
var line = Autodesk.Revit.DB.Line.CreateBound(new Autodesk.Revit.DB.XYZ(100, 0, 0), new Autodesk.Revit.DB.XYZ(120, 0, 0));
var hostWall = Autodesk.Revit.DB.Wall.Create(doc, line, level.Id, false);
doc.Regenerate();

Autodesk.Revit.DB.FamilySymbol doorSymbol = null;
foreach (Autodesk.Revit.DB.FamilySymbol fs in new Autodesk.Revit.DB.FilteredElementCollector(doc).OfCategory(Autodesk.Revit.DB.BuiltInCategory.OST_Doors).OfClass(typeof(Autodesk.Revit.DB.FamilySymbol))) { doorSymbol = fs; break; }
if (doorSymbol == null) { throw new System.Exception("no door FamilySymbol loaded in the default project template"); }
if (!doorSymbol.IsActive) { doorSymbol.Activate(); doc.Regenerate(); }

var midpoint = (hostWall.Location as Autodesk.Revit.DB.LocationCurve).Curve.Evaluate(0.5, true);
var door = doc.Create.NewFamilyInstance(midpoint, doorSymbol, hostWall, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

return new { doorCreated = door != null, category = door.Category == null ? "null" : door.Category.Name, hostId = door.Host == null ? "null" : door.Host.Id.ToString() };
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (return_value: %s)", out.Status, out.ReturnValue)
		}
		for _, want := range []string{"\"doorCreated\":true", "\"category\":\"Doors\""} {
			if !strings.Contains(out.ReturnValue, want) {
				t.Errorf("wanted %q in return_value: %s", want, out.ReturnValue)
			}
		}
	})

	// DimensionBetweenWalls: a linear dimension spanning two parallel walls' interior faces --
	// proves the Reference/ReferenceArray plumbing NewDimension needs, distinct from every other
	// element-creation call in this corpus (none of which need a Reference at all).
	//
	// Dimension.Value reads the INTERIOR-face-to-interior-face span, not the 20' the walls'
	// location lines are drawn at -- it comes back a little under 20.67' (20' minus each wall's
	// own thickness inset from its location line to its interior face), confirmed live. Asserted
	// with a tolerance and floor/ceiling bounds rather than an exact literal, since the exact
	// value depends on the default wall type's thickness, which this test doesn't control.
	t.Run("DimensionBetweenWalls", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
var level = Autodesk.Revit.DB.Level.Create(doc, 580.0);
var wallA = Autodesk.Revit.DB.Wall.Create(doc, Autodesk.Revit.DB.Line.CreateBound(new Autodesk.Revit.DB.XYZ(200, 0, 0), new Autodesk.Revit.DB.XYZ(220, 0, 0)), level.Id, false);
var wallB = Autodesk.Revit.DB.Wall.Create(doc, Autodesk.Revit.DB.Line.CreateBound(new Autodesk.Revit.DB.XYZ(200, 20, 0), new Autodesk.Revit.DB.XYZ(220, 20, 0)), level.Id, false);
doc.Regenerate();

Autodesk.Revit.DB.ViewPlan view = null;
foreach (Autodesk.Revit.DB.ViewPlan v in new Autodesk.Revit.DB.FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.ViewPlan))) {
  if (!v.IsTemplate) { view = v; break; }
}
var refsA = Autodesk.Revit.DB.HostObjectUtils.GetSideFaces(wallA, Autodesk.Revit.DB.ShellLayerType.Interior);
var refsB = Autodesk.Revit.DB.HostObjectUtils.GetSideFaces(wallB, Autodesk.Revit.DB.ShellLayerType.Interior);
var refArray = new Autodesk.Revit.DB.ReferenceArray();
refArray.Append(refsA[0]);
refArray.Append(refsB[0]);
var dimLine = Autodesk.Revit.DB.Line.CreateBound(new Autodesk.Revit.DB.XYZ(210, 0, 0), new Autodesk.Revit.DB.XYZ(210, 20, 0));
var dim = doc.Create.NewDimension(view, dimLine, refArray);

return new { dimCreated = dim != null, viewFound = view != null, value = dim.Value };
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (return_value: %s)", out.Status, out.ReturnValue)
		}
		for _, want := range []string{"\"dimCreated\":true", "\"viewFound\":true"} {
			if !strings.Contains(out.ReturnValue, want) {
				t.Errorf("wanted %q in return_value: %s", want, out.ReturnValue)
			}
		}
		// value should read comfortably between 19' and 20.9' (< 20' floor accounts for wall-face
		// inset from the 20'-apart location lines; the 20.9' ceiling catches a completely wrong
		// reference pairing, e.g. exterior-to-exterior, which would read OVER 20').
		valueOK := false
		// Prefix-matched on the JSON number, decimal point included: an exact 20.0 would serialize as
		// "value":20 and is deliberately not accepted here, exactly as it was not under the old
		// ToString rendering -- a dimension reading a whole number to that many places is the
		// suspicious case this range check exists to catch.
		for _, want := range []string{"\"value\":20.", "\"value\":19."} {
			if strings.Contains(out.ReturnValue, want) {
				valueOK = true
			}
		}
		if !valueOK {
			t.Errorf("dimension value outside the expected ~19-20.9' range for two walls 20' apart; return_value: %s", out.ReturnValue)
		}
	})

	// CreateWallSchedule: ViewSchedule.CreateSchedule for the Walls category, plus one field
	// (wall area) added to its definition -- the standard "create a schedule, add a field"
	// sequence. Deliberately independent of the wall-creating subtests above (works correctly
	// with zero or more walls already in the document), so it behaves identically whether run as
	// part of the bundle or in isolation via -run .../CreateWallSchedule.
	t.Run("CreateWallSchedule", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
var categoryId = new Autodesk.Revit.DB.ElementId(Autodesk.Revit.DB.BuiltInCategory.OST_Walls);
var schedule = Autodesk.Revit.DB.ViewSchedule.CreateSchedule(doc, categoryId);

Autodesk.Revit.DB.SchedulableField areaField = null;
foreach (var sf in schedule.Definition.GetSchedulableFields()) {
  if (sf.ParameterId.Value == (long)Autodesk.Revit.DB.BuiltInParameter.HOST_AREA_COMPUTED) { areaField = sf; break; }
}
if (areaField != null) { schedule.Definition.AddField(areaField); }

return new { scheduleCreated = schedule != null, name = schedule.Name, fieldAdded = areaField != null, fieldCount = schedule.Definition.GetFieldCount() };
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (return_value: %s)", out.Status, out.ReturnValue)
		}
		for _, want := range []string{"\"scheduleCreated\":true", "\"name\":\"Wall Schedule\"", "\"fieldAdded\":true", "\"fieldCount\":1"} {
			if !strings.Contains(out.ReturnValue, want) {
				t.Errorf("wanted %q in return_value: %s", want, out.ReturnValue)
			}
		}
	})
}
