//go:build harness

package harness_test

import (
	"strconv"
	"strings"
	"testing"
)

// TestValidationCorpus_ExportViewToDwg is validation-corpus case #1 (PRD §13 phase 4,
// revit/docs/validation-corpus.md): "export the active view to DWG," graded by whether
// search_functions/describe_function alone -- with no prior knowledge of the answer baked into this
// script -- were enough to find the right API. They were, on the FIRST query:
// search_functions("export view to dwg") surfaced Document.Export(folder, name, views,
// DWGExportOptions) at rank 6 of the first page and DWGExportOptions itself at rank 1; no
// reformulation was needed.
//
// One real discovery-tool GAP found in that same research -- SINCE CLOSED by issue #91, and kept here
// as the record of why the connector's API moved behind `Connector`. At the time,
// `Publish`/`ExportsDirectory` (this connector's own script globals, PRD §09) were invisible to
// list_functions/search_functions entirely -- those tools only reflected the RevitAPI corpus, not
// ScriptGlobals itself, so an agent had no discovery path to them at all short of already knowing they
// existed (from get_skills, or from a prior script's own compile error naming `doc` as undefined, as
// happened live during this case's own research -- ScriptGlobals.Document is the real global, `doc` is
// a fixture-only local alias this file's OWN preambles define, not a name to write freehand elsewhere).
// Filed as a discovery-coverage gap, not fixed here: PRD §13's grading protocol says a rough discovery
// path is a real product gap even when the case still passes.
func TestValidationCorpus_ExportViewToDwg(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
var level = Autodesk.Revit.DB.Level.Create(doc, 0.0);
Autodesk.Revit.DB.ElementId vftId = null;
foreach (Autodesk.Revit.DB.ViewFamilyType vft in new Autodesk.Revit.DB.FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.ViewFamilyType))) {
  if (vft.ViewFamily == Autodesk.Revit.DB.ViewFamily.FloorPlan) { vftId = vft.Id; break; }
}
var view = Autodesk.Revit.DB.ViewPlan.Create(doc, vftId, level.Id);
doc.Regenerate();

var options = new Autodesk.Revit.DB.DWGExportOptions();
var views = new System.Collections.Generic.List<Autodesk.Revit.DB.ElementId> { view.Id };
var exportName = "validation-case01-" + System.Guid.NewGuid().ToString("N");
var ok = doc.Export(Connector.ExportsDirectory, exportName, views, options);
var dwgPath = System.IO.Path.Combine(Connector.ExportsDirectory, exportName + ".dwg");
var exists = System.IO.File.Exists(dwgPath);
long size = exists ? new System.IO.FileInfo(dwgPath).Length : 0;

return new { exported = ok, dwgExists = exists, dwgSize = size, exportName };
`)
	if out.Status != "success" {
		t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
	}
	for _, want := range []string{"exported = True", "dwgExists = True"} {
		if !strings.Contains(out.Output, want) {
			t.Errorf("wanted %q in output: %s", want, out.Output)
		}
	}
	if strings.Contains(out.Output, "dwgSize = 0") {
		t.Errorf("exported DWG must be non-empty (a False export or a zero-byte file both read as 'exists' but neither is a real export): %s", out.Output)
	}

	// The exported .dwg (and Revit's own .pcp plot-color sidecar alongside it) are cleanup, not
	// assertion -- deleting them keeps repeated local runs from accumulating files in the shared
	// exports directory, but their presence/absence has already been checked above.
	t.Cleanup(func() {
		cleanup := `
var dir = Connector.ExportsDirectory;
foreach (var f in System.IO.Directory.GetFiles(dir, "validation-case01-*")) {
  try { System.IO.File.Delete(f); } catch {}
}
return "cleaned";
`
		_ = callExecuteScriptWith(t, c, instanceID, documentID, cleanup, nil)
	})
}

// TestValidationCorpus_ClosedRectangularFootprint is validation-corpus case #2: "model a closed
// rectangular building footprint (4 walls forming a loop) and confirm they join at corners." Discovery
// path: search_functions("join walls at corner") did NOT surface JoinGeometryUtils (that query's top
// results were mostly BuiltInParameter/PostableCommand noise) -- a second, more specific query,
// search_functions("AreElementsJoined geometry"), found JoinGeometryUtils.AreElementsJoined at rank 1.
//
// REAL FINDING, and the reason this case asserts WallUtils.IsWallJoinAllowedAtEnd rather than
// JoinGeometryUtils.AreElementsJoined: they measure two different things. Four walls created with
// coincident corner endpoints (the closed loop itself) tested FALSE for
// JoinGeometryUtils.AreElementsJoined at every corner live -- that API tracks solid BOOLEAN union (the
// kind used for wall-to-floor cuts), not the wall END-JOIN miter cleanup a closed footprint's corners
// actually need. WallUtils.IsWallJoinAllowedAtEnd is the right check: it was True at both ends of every
// wall (Revit's default), which is what lets Revit apply the automatic corner miter at all. A first
// discovery pass reaching for the wrong "joined" API and getting a clean False for every corner, on a
// footprint that visibly closes, is exactly the kind of gap PRD §13 wants this corpus to surface.
func TestValidationCorpus_ClosedRectangularFootprint(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
var level = Autodesk.Revit.DB.Level.Create(doc, 0.0);
Autodesk.Revit.DB.WallType wallType = null;
foreach (Autodesk.Revit.DB.WallType wt in new Autodesk.Revit.DB.FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.WallType))) {
  if (wt.Kind == Autodesk.Revit.DB.WallKind.Basic) { wallType = wt; break; }
}

var p1 = new Autodesk.Revit.DB.XYZ(0, 0, 0);
var p2 = new Autodesk.Revit.DB.XYZ(20, 0, 0);
var p3 = new Autodesk.Revit.DB.XYZ(20, 15, 0);
var p4 = new Autodesk.Revit.DB.XYZ(0, 15, 0);

var w1 = Autodesk.Revit.DB.Wall.Create(doc, Autodesk.Revit.DB.Line.CreateBound(p1, p2), wallType.Id, level.Id, 10.0, 0.0, false, false);
var w2 = Autodesk.Revit.DB.Wall.Create(doc, Autodesk.Revit.DB.Line.CreateBound(p2, p3), wallType.Id, level.Id, 10.0, 0.0, false, false);
var w3 = Autodesk.Revit.DB.Wall.Create(doc, Autodesk.Revit.DB.Line.CreateBound(p3, p4), wallType.Id, level.Id, 10.0, 0.0, false, false);
var w4 = Autodesk.Revit.DB.Wall.Create(doc, Autodesk.Revit.DB.Line.CreateBound(p4, p1), wallType.Id, level.Id, 10.0, 0.0, false, false);
doc.Regenerate();

var walls = new[] { w1, w2, w3, w4 };
var allEndsAllowJoin = true;
foreach (var w in walls) {
  if (!Autodesk.Revit.DB.WallUtils.IsWallJoinAllowedAtEnd(w, 0)) allEndsAllowJoin = false;
  if (!Autodesk.Revit.DB.WallUtils.IsWallJoinAllowedAtEnd(w, 1)) allEndsAllowJoin = false;
}

// The loop closure itself: each wall's end coincides with the next wall's start.
var loopCloses =
  ((Autodesk.Revit.DB.LocationCurve)w1.Location).Curve.GetEndPoint(1).IsAlmostEqualTo(((Autodesk.Revit.DB.LocationCurve)w2.Location).Curve.GetEndPoint(0)) &&
  ((Autodesk.Revit.DB.LocationCurve)w2.Location).Curve.GetEndPoint(1).IsAlmostEqualTo(((Autodesk.Revit.DB.LocationCurve)w3.Location).Curve.GetEndPoint(0)) &&
  ((Autodesk.Revit.DB.LocationCurve)w3.Location).Curve.GetEndPoint(1).IsAlmostEqualTo(((Autodesk.Revit.DB.LocationCurve)w4.Location).Curve.GetEndPoint(0)) &&
  ((Autodesk.Revit.DB.LocationCurve)w4.Location).Curve.GetEndPoint(1).IsAlmostEqualTo(((Autodesk.Revit.DB.LocationCurve)w1.Location).Curve.GetEndPoint(0));

return new { wallTypeFound = wallType != null, wallCount = walls.Length, loopCloses, allEndsAllowJoin };
`)
	if out.Status != "success" {
		t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
	}
	for _, want := range []string{"wallTypeFound = True", "wallCount = 4", "loopCloses = True", "allEndsAllowJoin = True"} {
		if !strings.Contains(out.Output, want) {
			t.Errorf("wanted %q in output: %s", want, out.Output)
		}
	}
}

// TestValidationCorpus_LoadFamilyAndPlaceInstance is validation-corpus case #3: "load a family from a
// library path and place an instance of it." This dev machine's installed content library turned out
// to be a stub (446 .rfa files across all languages, mostly localized placeholder/redirect content, no
// real furniture/casework families) -- discovered live via Directory.GetFiles rather than assumed, per
// PRD §13's grading protocol. Rather than depend on optional Revit content-library installs a corpus
// case shouldn't be fragile against, this case builds its OWN minimal family (a Generic Model box
// extrusion from the "Generic Model.rft" template, found via Application.FamilyTemplatePath) and loads
// THAT -- still a genuine, complete exercise of Document.LoadFamily + FamilySymbol activation +
// NewFamilyInstance, the actual API surface the task is about.
//
// TWO REAL FINDINGS from live research, both about this connector's ambient-transaction model
// interacting with Document.LoadFamily specifically, not generic Revit API behavior:
//
//  1. Document.LoadFamily(Document) requires its SOURCE document to have NO open transaction. A family
//     document created via CreateFamilyDocument in the SAME script call as its own geometry edits still
//     has its managed transaction open for the rest of that call -- LoadFamily on it in the same call
//     throws "The document must not be modifiable before calling LoadFamily." The fix is the same
//     two-call split OpenForWriting itself exists for (see fixtureWritePreamble's own doc comment): build
//     the family in one execute_script call, let that call return so the transaction commits and closes,
//     then LoadFamily it from a SEPARATE call.
//  2. Less obvious, and NOT fixed by the two-call split alone: LoadFamily ALSO requires its TARGET
//     document to have no open transaction at the moment of the call. Calling OpenForWriting(doc) before
//     famDoc.LoadFamily(doc) throws the identical error, just naming the target instead of the source.
//     The correct order within the second call is LoadFamily first, THEN OpenForWriting(doc) -- the
//     family instance placement that follows needs the transaction, but the load itself must happen
//     before it opens.
func TestValidationCorpus_LoadFamilyAndPlaceInstance(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	build := runScript(t, c, instanceID, documentID, `
var app = UIApplication.Application;
string template = "";
foreach (var f in System.IO.Directory.EnumerateFiles(app.FamilyTemplatePath, "Generic Model.rft", System.IO.SearchOption.AllDirectories)) {
  template = f; break;
}
if (template.Length == 0) return "no-template";

var famDoc = Connector.CreateFamilyDocument(template);
var plane = Autodesk.Revit.DB.Plane.CreateByNormalAndOrigin(Autodesk.Revit.DB.XYZ.BasisZ, Autodesk.Revit.DB.XYZ.Zero);
var sketchPlane = Autodesk.Revit.DB.SketchPlane.Create(famDoc, plane);

var p1 = new Autodesk.Revit.DB.XYZ(-1, -1, 0);
var p2 = new Autodesk.Revit.DB.XYZ(1, -1, 0);
var p3 = new Autodesk.Revit.DB.XYZ(1, 1, 0);
var p4 = new Autodesk.Revit.DB.XYZ(-1, 1, 0);
var profile = new Autodesk.Revit.DB.CurveArray();
profile.Append(Autodesk.Revit.DB.Line.CreateBound(p1, p2));
profile.Append(Autodesk.Revit.DB.Line.CreateBound(p2, p3));
profile.Append(Autodesk.Revit.DB.Line.CreateBound(p3, p4));
profile.Append(Autodesk.Revit.DB.Line.CreateBound(p4, p1));
var profileArr = new Autodesk.Revit.DB.CurveArrArray();
profileArr.Append(profile);

var extrusion = famDoc.FamilyCreate.NewExtrusion(true, profileArr, sketchPlane, 2.0);
famDoc.Regenerate();

return $"famTitle={famDoc.Title} extrusionCreated={extrusion != null}";
`)
	if build.Status != "success" {
		t.Fatalf("family-build call: expected status=success, got %q (output: %s)", build.Status, build.Output)
	}
	if strings.Contains(build.Output, "no-template") {
		t.Skip("no \"Generic Model.rft\" under Application.FamilyTemplatePath on this machine")
	}
	if !strings.Contains(build.Output, "extrusionCreated=True") {
		t.Fatalf("extrusion was not created: %s", build.Output)
	}
	famTitle := ""
	for _, field := range strings.Split(build.Output, " ") {
		if strings.HasPrefix(field, "famTitle=") {
			famTitle = strings.TrimPrefix(field, "famTitle=")
		}
	}
	if famTitle == "" {
		t.Fatalf("could not parse famTitle from build output: %s", build.Output)
	}

	loadScript := fixtureLookupPreamble(fixtureTitle) + `
Autodesk.Revit.DB.Document famDoc = null;
foreach (Autodesk.Revit.DB.Document candidate in UIApplication.Application.Documents) {
  if (candidate.Title == ` + strconv.Quote(famTitle) + `) { famDoc = candidate; }
}
if (famDoc == null) { throw new System.Exception("family document not found by title: " + ` + strconv.Quote(famTitle) + `); }

var family = famDoc.LoadFamily(doc);
famDoc.Close(false);

Connector.OpenForWriting(doc);

Autodesk.Revit.DB.FamilySymbol symbol = null;
foreach (Autodesk.Revit.DB.ElementId symId in family.GetFamilySymbolIds()) {
  symbol = doc.GetElement(symId) as Autodesk.Revit.DB.FamilySymbol;
  break;
}
if (symbol != null && !symbol.IsActive) { symbol.Activate(); }
doc.Regenerate();

var instance = doc.Create.NewFamilyInstance(new Autodesk.Revit.DB.XYZ(5, 5, 0), symbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
doc.Regenerate();

var placedCount = new Autodesk.Revit.DB.FilteredElementCollector(doc)
  .OfCategory(Autodesk.Revit.DB.BuiltInCategory.OST_GenericModel)
  .WhereElementIsNotElementType()
  .GetElementCount();

return new {
  familyLoaded = family != null,
  symbolFound = symbol != null,
  symbolActive = symbol != null && symbol.IsActive,
  instanceCreated = instance != null,
  placedCount
};
`
	// Close(false) is confirm_lifecycle_actions-gated (PRD §14) -- it acts outside the ambient
	// transaction boundary, same as every other Document.Close call in this suite.
	load := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, instanceID, documentID, loadScript,
		map[string]any{"confirm_lifecycle_actions": true}))
	if load.Status != "success" {
		t.Fatalf("load/place call: expected status=success, got %q (output: %s)", load.Status, load.Output)
	}
	for _, want := range []string{"familyLoaded = True", "symbolFound = True", "symbolActive = True", "instanceCreated = True", "placedCount = 1"} {
		if !strings.Contains(load.Output, want) {
			t.Errorf("wanted %q in output: %s", want, load.Output)
		}
	}
}
