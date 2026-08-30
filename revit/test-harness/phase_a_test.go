//go:build harness

package harness_test

import (
	"strings"
	"testing"
)

// TestPhaseACoreCRUDAndQuery is the first fixture-system bundle (coverage
// plan, "Rollout order" step 4) -- one blank document created ONCE via
// createBlankFixtureDocument, then each check below as its own INDEPENDENT
// t.Run subtest, each its own execute_script call, against that same
// document -- found again by Title per subtest (fixtureLookupPreamble) and
// opened for writing per subtest (fixtureWritePreamble, ScriptGlobals.
// OpenForWriting) since a document's managed transaction from the call that
// created it commits and closes the moment that call returns. Subtests are
// independent, not chained: each creates whatever elements it needs at its
// own elevation AND X-offset (elevation alone isn't enough -- two walls
// sharing the same X/Y footprint at different levels still overlap in 3D
// once default wall height is accounted for, confirmed live as a harmless
// but noisy "walls overlap" warning) so a failure in one does not cascade
// into another, and so re-running a single subtest in isolation
// (`-run TestPhaseACoreCRUDAndQuery/CreateWall`) behaves the same as running
// the whole bundle.
//
// Every script below was executed live via mcp__revit__execute_script
// against a real connected instance before being committed here (this
// project's standing "no fake integration tier" rule extends to test
// AUTHORING, not just to what ships) -- see PRD §13's corpus plan for the
// live-research sessions that shaped the CreateSharedParameter and
// EditGroupPropagatesToAllInstances cases, and the OpenForWriting feature
// this bundle now depends on.
func TestPhaseACoreCRUDAndQuery(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)
	fixtureTitle := createBlankFixtureDocument(t, c, instanceID, documentID)

	// CreateWall proves the most basic write pattern every other subtest in
	// this bundle (and every later phase) leans on: find the fixture document,
	// open it for writing, create a supporting element (a Level, to host the
	// wall), create the element under test, and read a property back to
	// confirm it landed.
	t.Run("CreateWall", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
var level = Autodesk.Revit.DB.Level.Create(doc, 10.0);
var line = Autodesk.Revit.DB.Line.CreateBound(
    new Autodesk.Revit.DB.XYZ(0, 0, 0), new Autodesk.Revit.DB.XYZ(20, 0, 0));
var wall = Autodesk.Revit.DB.Wall.Create(doc, line, level.Id, false);
return new {
  created = wall != null && wall.Id.Value != 0,
  category = wall.Category == null ? "null" : wall.Category.Name
};
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
		}
		for _, want := range []string{"created = True", "category = Walls"} {
			if !strings.Contains(out.Output, want) {
				t.Errorf("wanted %q in output: %s", want, out.Output)
			}
		}
	})

	// QueryElementsByCategory proves the FilteredElementCollector + category
	// filter pattern every later query-shaped case in this corpus will reuse.
	t.Run("QueryElementsByCategory", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
var level = Autodesk.Revit.DB.Level.Create(doc, 20.0);
var line = Autodesk.Revit.DB.Line.CreateBound(
    new Autodesk.Revit.DB.XYZ(100, 0, 0), new Autodesk.Revit.DB.XYZ(115, 0, 0));
Autodesk.Revit.DB.Wall.Create(doc, line, level.Id, false);
var count = new Autodesk.Revit.DB.FilteredElementCollector(doc)
    .OfCategory(Autodesk.Revit.DB.BuiltInCategory.OST_Walls)
    .WhereElementIsNotElementType()
    .GetElementCount();
return new { foundAtLeastOne = count >= 1, count };
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
		}
		if !strings.Contains(out.Output, "foundAtLeastOne = True") {
			t.Errorf("category query did not find the wall it just created; output: %s", out.Output)
		}
	})

	// GetSetParameter: set a built-in instance parameter, read it back in the
	// SAME script (the round-trip a single execute_script call can prove --
	// whether the value survives to a LATER script is TransactionScriptExecutor's
	// job, already covered end-to-end by TestCreatedDocumentIsWritable and,
	// now, by this whole bundle's own use of OpenForWriting across calls).
	t.Run("GetSetParameter", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
var level = Autodesk.Revit.DB.Level.Create(doc, 30.0);
var line = Autodesk.Revit.DB.Line.CreateBound(
    new Autodesk.Revit.DB.XYZ(200, 0, 0), new Autodesk.Revit.DB.XYZ(210, 0, 0));
var wall = Autodesk.Revit.DB.Wall.Create(doc, line, level.Id, false);
var p = wall.get_Parameter(Autodesk.Revit.DB.BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
p.Set("mcp-harness-phase-a");
var readBack = wall.get_Parameter(Autodesk.Revit.DB.BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).AsString();
return new { roundTripped = readBack == "mcp-harness-phase-a", readBack };
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
		}
		if !strings.Contains(out.Output, "roundTripped = True") {
			t.Errorf("parameter set/get round-trip failed; output: %s", out.Output)
		}
	})

	// DeleteElement: create, delete, and confirm doc.GetElement can no longer
	// find it -- the negative-space counterpart to every creation check above.
	t.Run("DeleteElement", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
var level = Autodesk.Revit.DB.Level.Create(doc, 40.0);
var line = Autodesk.Revit.DB.Line.CreateBound(
    new Autodesk.Revit.DB.XYZ(300, 0, 0), new Autodesk.Revit.DB.XYZ(308, 0, 0));
var wall = Autodesk.Revit.DB.Wall.Create(doc, line, level.Id, false);
var id = wall.Id;
doc.Delete(id);
var stillThere = doc.GetElement(id) != null;
return new { deleted = !stillThere };
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
		}
		if !strings.Contains(out.Output, "deleted = True") {
			t.Errorf("element was not actually deleted; output: %s", out.Output)
		}
	})

	// CreateSharedParameter: define one text parameter in a fresh shared-
	// parameter file and bind it into the document's own BindingMap against
	// the Walls category.
	//
	// TWO REAL API CORRECTIONS FROM LIVE TESTING, both against Application
	// (Autodesk.Revit.ApplicationServices.Application):
	//   1. There is NO Application.CreateSharedParameterFile() -- confirmed
	//      via mcp__revit__search_functions, which lists only
	//      OpenSharedParameterFile() and the SharedParametersFilename
	//      property. A shared-parameter file is a plain tab-delimited text
	//      file Revit reads, not something the API can generate from
	//      scratch -- so this script writes it directly with System.IO
	//      (including the PARAM row, so no ExternalDefinitionCreationOptions/
	//      Definitions.Create call is needed either -- just read the
	//      already-defined parameter back via Definitions.get_Item), then
	//      opens it with OpenSharedParameterFile().
	//   2. Autodesk.Revit.DB.BuiltInParameterGroup does not exist in this
	//      API version (Revit 2025+) -- BindingMap.Insert's third parameter
	//      is a ForgeTypeId, and GroupTypeId.Data is the modern replacement
	//      for the old BuiltInParameterGroup.PG_DATA.
	// Independent PR review finding: this used to set app.SharedParametersFilename and never
	// restore it, and never deleted the temp file it wrote -- a real, process-wide Revit
	// application setting left pointed at a file this test created in the OS temp directory,
	// leaking both the setting change (affecting whatever a human or another script does next in
	// this same Revit session) and the file itself across every run. Now captures the original
	// value up front and restores it, and deletes the temp file, in a try/finally so both happen
	// even if OpenSharedParameterFile or the binding calls below throw.
	t.Run("CreateSharedParameter", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
var app = UIApplication.Application;
var sharedParamPath = System.IO.Path.Combine(
    System.IO.Path.GetTempPath(), "mcp-harness-shared-params-" + System.Guid.NewGuid().ToString("N") + ".txt");
var guid = System.Guid.NewGuid().ToString();
var fileContent = "# This is a Revit shared parameter file.\n# Do not edit manually.\n*META\tVERSION\tMINVERSION\nMETA\t2\t1\n*GROUP\tID\tNAME\nGROUP\t1\tMCPHarness\n*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\nPARAM\t" + guid + "\tMCPHarnessTestParam\tTEXT\t\t1\t1\t\t1\n";
System.IO.File.WriteAllText(sharedParamPath, fileContent);
var originalSharedParamsFilename = app.SharedParametersFilename;
bool bound;
string definitionName;
try {
    app.SharedParametersFilename = sharedParamPath;
    var defFile = app.OpenSharedParameterFile();
    var group = defFile.Groups.get_Item("MCPHarness");
    var definition = group.Definitions.get_Item("MCPHarnessTestParam") as Autodesk.Revit.DB.ExternalDefinition;
    // Independent PR review finding: the old ternary form of definitionName below this point was
    // dead -- ParameterBindings.Insert(definition, ...) already dereferences
    // definition internally, so a null would have thrown there first, before this line could ever be
    // reached with definition still null. An explicit, fail-fast check here (right after the cast that
    // could actually produce null) is honest about what's really being guarded against, instead of
    // relying on an undocumented assumption about what Insert happens to do with a null argument.
    if (definition == null) { throw new System.Exception("shared parameter definition not found after OpenSharedParameterFile"); }

    var categorySet = app.Create.NewCategorySet();
    categorySet.Insert(doc.Settings.Categories.get_Item(Autodesk.Revit.DB.BuiltInCategory.OST_Walls));
    var binding = app.Create.NewInstanceBinding(categorySet);
    bound = doc.ParameterBindings.Insert(definition, binding, Autodesk.Revit.DB.GroupTypeId.Data);
    definitionName = definition.Name;
} finally {
    app.SharedParametersFilename = originalSharedParamsFilename;
    System.IO.File.Delete(sharedParamPath);
}

return new { bound, definitionName };
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
		}
		for _, want := range []string{"bound = True", "definitionName = MCPHarnessTestParam"} {
			if !strings.Contains(out.Output, want) {
				t.Errorf("wanted %q in output: %s", want, out.Output)
			}
		}
	})

	// EditGroupPropagatesToAllInstances: flagged in the coverage plan as
	// worth building EARLY -- a prior implementation reportedly had a real
	// problem here, and this connector's own ambient-transaction-per-script
	// wrapping was a plausible new source of interaction bugs. Live research
	// (this session, execute_script calls run interactively before this file
	// was written, plus corroborating web research) found the true shape of
	// the problem, which is
	// NOT what the original plan assumed:
	//
	//  - There is no group-edit-SCOPE API (no Document.EditGroup, nothing
	//    resembling Document.EditFamily for groups). Confirmed against
	//    Autodesk's own Revit API forum and Jeremy Tammik's Building Coder
	//    blog (the canonical Revit API reference): this is a long-standing,
	//    documented gap in the public API, not something this session missed.
	//  - Editing a group MEMBER directly (no scope) is nonetheless a real,
	//    working operation, gated by Revit's own consistency rules: with
	//    exactly ONE placed instance of the GroupType, Revit allows the edit
	//    with a "changed outside group edit mode... allowed because there is
	//    only one instance" WARNING; with TWO OR MORE instances, the same
	//    edit is correctly REFUSED as an ERROR ("Changes to groups are
	//    allowed only in group edit mode... Use the Ungroup option..."),
	//    which our connector's failure-handling resolves as a rollback
	//    (status=error), not a silent no-op -- Revit is actively protecting
	//    group consistency here, not failing to enforce it.
	//  - ElementTransformUtils.MoveElement on an individual MEMBER's own
	//    ElementId is a SEPARATE, unrelated trap: it silently does nothing at
	//    all (no warning, no error, no effect), confirmed live and matching
	//    an independent Revit Add-Ons blog report of the same behavior for
	//    detail groups. Moving the GROUP's own ElementId (optionally via
	//    ElementTransformUtils.MoveElements with the group id plus member
	//    ids) performs a correct rigid-body move of the whole placed
	//    instance instead -- confirmed live. This subtest does not exercise
	//    that path at all, specifically to avoid mixing two independent
	//    findings into one assertion.
	//  - The REAL, fully API-achievable way to make an edit propagate to
	//    OTHER existing instances: Group.UngroupMembers() the instance whose
	//    shape you want to change (its members become ordinary loose
	//    elements, no restriction), edit them freely, doc.Create.NewGroup(...)
	//    them again to mint a NEW GroupType reflecting the edit, then set
	//    otherInstance.GroupType = newGroupType on every other placed
	//    instance that should pick up the change -- Group.GroupType has a
	//    public setter. Confirmed live: after reassignment, the OTHER
	//    instance's member elements are Revit-recreated (their ElementIds
	//    change -- re-fetch GetMemberIds() after reassigning, never reuse an
	//    id captured beforehand) with geometry that is bit-for-bit identical
	//    to a BRAND NEW instance placed fresh from the same new GroupType at
	//    the same origin -- the actual "does it now match its prototype"
	//    proof this subtest asserts, sidestepping PlaceGroup's own origin-
	//    offset behavior (a placed instance's members are NOT at the
	//    absolute coordinates you might naively expect from the origin
	//    argument alone -- compare two instances of the SAME type against
	//    each other, never against an assumed absolute coordinate).
	t.Run("EditGroupPropagatesToAllInstances", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, fixtureWritePreamble(fixtureTitle)+`
var level = Autodesk.Revit.DB.Level.Create(doc, 50.0);
var line = Autodesk.Revit.DB.Line.CreateBound(
    new Autodesk.Revit.DB.XYZ(400, 0, 0), new Autodesk.Revit.DB.XYZ(410, 0, 0));
var wall = Autodesk.Revit.DB.Wall.Create(doc, line, level.Id, false);
doc.Regenerate();

var group1 = doc.Create.NewGroup(new System.Collections.Generic.List<Autodesk.Revit.DB.ElementId> { wall.Id });
doc.Regenerate();
var originalType = group1.GroupType;

// A second placed instance, elsewhere -- the "other instance" this subtest
// checks actually picks up the edit once it's applied via type reassignment.
var group2 = doc.Create.PlaceGroup(new Autodesk.Revit.DB.XYZ(400, 300, 0), originalType);
doc.Regenerate();
var group2Id = group2.Id;

// Ungroup instance 1, edit its now-loose wall (unrestricted -- no longer a
// group member), regroup into a NEW type that reflects the edit.
var ungroupedIds = group1.UngroupMembers();
Autodesk.Revit.DB.ElementId looseWallId = null;
foreach (var id in ungroupedIds) { if (doc.GetElement(id) is Autodesk.Revit.DB.Wall) { looseWallId = id; } }
var looseLc = (doc.GetElement(looseWallId) as Autodesk.Revit.DB.Wall).Location as Autodesk.Revit.DB.LocationCurve;
looseLc.Curve = Autodesk.Revit.DB.Line.CreateBound(
    new Autodesk.Revit.DB.XYZ(403, 0, 0), new Autodesk.Revit.DB.XYZ(413, 0, 0));
doc.Regenerate();
var newGroup = doc.Create.NewGroup(new System.Collections.Generic.List<Autodesk.Revit.DB.ElementId> { looseWallId });
doc.Regenerate();
var newTypeId = newGroup.GroupType.Id;

// Reassign instance 2 to the new type -- this is the actual propagation step.
var group2Fresh = doc.GetElement(group2Id) as Autodesk.Revit.DB.Group;
group2Fresh.GroupType = doc.GetElement(newTypeId) as Autodesk.Revit.DB.GroupType;
doc.Regenerate();

// A THIRD instance, freshly placed from the new type at the SAME origin as
// instance 2 -- the correct comparison baseline (see the C# test's own
// comment on PlaceGroup's origin-offset behavior; comparing against an
// assumed absolute coordinate produced a false negative during live
// research).
var group3 = doc.Create.PlaceGroup(new Autodesk.Revit.DB.XYZ(400, 300, 0), doc.GetElement(newTypeId) as Autodesk.Revit.DB.GroupType);
doc.Regenerate();

double? group2WallX = null;
foreach (var id in (doc.GetElement(group2Id) as Autodesk.Revit.DB.Group).GetMemberIds()) {
  if (doc.GetElement(id) is Autodesk.Revit.DB.Wall w) { group2WallX = (w.Location as Autodesk.Revit.DB.LocationCurve).Curve.GetEndPoint(0).X; }
}
double? group3WallX = null;
foreach (var id in group3.GetMemberIds()) {
  if (doc.GetElement(id) is Autodesk.Revit.DB.Wall w) { group3WallX = (w.Location as Autodesk.Revit.DB.LocationCurve).Curve.GetEndPoint(0).X; }
}

// Independent PR review finding: exact double equality on Revit-regenerated geometry is a live
// flake risk (a regenerate-order change could shift either value by float noise with no diagnostic),
// even though it has been reliable in live testing so far. A small tolerance, plus both raw values in
// the output, keeps this passing for the same real cases while giving something to look at if it
// ever does drift.
var deltaX = group2WallX.HasValue && group3WallX.HasValue ? System.Math.Abs(group2WallX.Value - group3WallX.Value) : (double?)null;
return new {
  group2HasMember = group2WallX.HasValue,
  group3HasMember = group3WallX.HasValue,
  group2WallX,
  group3WallX,
  propagated = deltaX.HasValue && deltaX.Value < 0.0001
};
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (output: %s)", out.Status, out.Output)
		}
		if !strings.Contains(out.Output, "group2HasMember = True") || !strings.Contains(out.Output, "group3HasMember = True") {
			t.Fatalf("one of the group instances reported no member wall at all; output: %s", out.Output)
		}
		if !strings.Contains(out.Output, "propagated = True") {
			t.Errorf("reassigning Group.GroupType did not make the existing instance match a fresh instance of the same (edited) type -- output: %s", out.Output)
		}
	})
}
