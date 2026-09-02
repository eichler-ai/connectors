//go:build harness

package harness_test

import (
	"fmt"
	"regexp"
	"strconv"
	"strings"
	"testing"
)

// TestSelfTransactingApiBetweenBlocksSurvivesTheRun pins the independent-review
// finding on #160: Document.LoadFamily (like EditScope.Commit and Export) commits
// ITS OWN transaction into the run's group between blocks. The connector never
// sees a transaction close, so with CommittedCount as the only signal the group
// looked empty and was rolled back at run end -- unloading the family again while
// reporting success. The fix routes the run's DocumentChanged observations into
// ManagedDocumentTransactions; this test proves the load is still there in the
// NEXT call, which is the only place the rollback would have shown.
func TestSelfTransactingApiBetweenBlocksSurvivesTheRun(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	probe := runScript(t, c, instanceID, documentID, `
var app = UIApplication.Application;
foreach (var f in System.IO.Directory.EnumerateFiles(app.FamilyTemplatePath, "Generic Model.rft", System.IO.SearchOption.AllDirectories)) { return f; }
return "";
`)
	template := strings.TrimSpace(strings.Trim(probe.ReturnValue, `"`))
	if template == "" {
		t.Skip("no Generic Model.rft under Application.FamilyTemplatePath; cannot build a family to load")
	}

	// Call 1: build a family document (its geometry in a block) and load it into the
	// routed document at TOP LEVEL -- the load is the run's only commit into the
	// ambient group, and it is not a connector transaction.
	loaded := runScript(t, c, instanceID, documentID, fmt.Sprintf(`
var fam = Connector.CreateFamilyDocument(%s);
System.Console.WriteLine("cleanup-title=" + fam.Title + ";");
Connector.WithTransaction(fam, () => {
  var plane = Autodesk.Revit.DB.Plane.CreateByNormalAndOrigin(Autodesk.Revit.DB.XYZ.BasisZ, Autodesk.Revit.DB.XYZ.Zero);
  var sketchPlane = Autodesk.Revit.DB.SketchPlane.Create(fam, plane);
  var profile = new Autodesk.Revit.DB.CurveArray();
  var p1 = new Autodesk.Revit.DB.XYZ(-1, -1, 0); var p2 = new Autodesk.Revit.DB.XYZ(1, -1, 0);
  var p3 = new Autodesk.Revit.DB.XYZ(1, 1, 0); var p4 = new Autodesk.Revit.DB.XYZ(-1, 1, 0);
  profile.Append(Autodesk.Revit.DB.Line.CreateBound(p1, p2)); profile.Append(Autodesk.Revit.DB.Line.CreateBound(p2, p3));
  profile.Append(Autodesk.Revit.DB.Line.CreateBound(p3, p4)); profile.Append(Autodesk.Revit.DB.Line.CreateBound(p4, p1));
  var arr = new Autodesk.Revit.DB.CurveArrArray(); arr.Append(profile);
  fam.FamilyCreate.NewExtrusion(true, arr, sketchPlane, 2.0);
});
var family = fam.LoadFamily(Document);
return new { loaded = family != null, name = family?.Name };
`, strconv.Quote(template)))
	if loaded.Status != "success" {
		t.Fatalf("expected status=success, got %q (%s)", loaded.Status, loaded.diag())
	}
	registerCreatedDocumentCleanup(t, c, instanceID, documentID, loaded.Output)
	if !strings.Contains(loaded.ReturnValue, `"loaded":true`) {
		t.Fatalf("LoadFamily at top level was expected to succeed under group-always; %s", loaded.diag())
	}
	t.Logf("call 1: %s", loaded.diag())
	// The loaded family takes its name from the family DOCUMENT, whatever OwnerFamily.Name was set to
	// mid-run; look it up by the name the load actually reported.
	m := regexp.MustCompile(`"name":"([^"]+)"`).FindStringSubmatch(loaded.ReturnValue)
	if m == nil {
		t.Fatalf("call 1 did not report the loaded family's name; %s", loaded.diag())
	}
	familyName := m[1]

	// Call 2: the family must still be loaded. Then remove it (inside a block) so the
	// fixture stays clean for the rest of the suite.
	check := runScript(t, c, instanceID, documentID, fmt.Sprintf(`
Autodesk.Revit.DB.Family found = null;
foreach (Autodesk.Revit.DB.Family f in new Autodesk.Revit.DB.FilteredElementCollector(Document).OfClass(typeof(Autodesk.Revit.DB.Family))) {
  if (f.Name == %s) { found = f; break; }
}
if (found == null) return "gone";
Connector.WithTransaction(Document, () => { Document.Delete(found.Id); });
return "still-loaded-then-removed";
`, strconv.Quote(familyName)))
	if check.Status != "success" {
		t.Fatalf("expected status=success, got %q (%s)", check.Status, check.diag())
	}
	if !strings.Contains(check.ReturnValue, "still-loaded-then-removed") {
		t.Fatalf("the family loaded at top level in the previous call was rolled back with the \"empty\" group -- the external-commit fix is not working; %s", check.diag())
	}
}

// TestAThrowAfterATopLevelLoadFamilyUndoesTheLoad is the other half of the
// external-commit rule, and the live check epic #146 listed before trusting
// Phase 3: a self-transacting API's commit sits INSIDE the run's group, so when
// the script throws afterwards the group rollback discards it along with
// everything else. Revit-level fact recorded in PRD §06; pinned here so a
// change to the group lifecycle cannot silently break the all-or-nothing
// promise for work the connector never transacted itself.
func TestAThrowAfterATopLevelLoadFamilyUndoesTheLoad(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	probe := runScript(t, c, instanceID, documentID, `
var app = UIApplication.Application;
foreach (var f in System.IO.Directory.EnumerateFiles(app.FamilyTemplatePath, "Generic Model.rft", System.IO.SearchOption.AllDirectories)) { return f; }
return "";
`)
	template := strings.TrimSpace(strings.Trim(probe.ReturnValue, `"`))
	if template == "" {
		t.Skip("no Generic Model.rft under Application.FamilyTemplatePath; cannot build a family to load")
	}

	rej := runRejectedScript(t, c, instanceID, documentID, fmt.Sprintf(`
var fam = Connector.CreateFamilyDocument(%s);
System.Console.WriteLine("cleanup-title=" + fam.Title + ";");
Connector.WithTransaction(fam, () => {
  var plane = Autodesk.Revit.DB.Plane.CreateByNormalAndOrigin(Autodesk.Revit.DB.XYZ.BasisZ, Autodesk.Revit.DB.XYZ.Zero);
  var sketchPlane = Autodesk.Revit.DB.SketchPlane.Create(fam, plane);
  var profile = new Autodesk.Revit.DB.CurveArray();
  var p1 = new Autodesk.Revit.DB.XYZ(-1, -1, 0); var p2 = new Autodesk.Revit.DB.XYZ(1, -1, 0);
  var p3 = new Autodesk.Revit.DB.XYZ(1, 1, 0); var p4 = new Autodesk.Revit.DB.XYZ(-1, 1, 0);
  profile.Append(Autodesk.Revit.DB.Line.CreateBound(p1, p2)); profile.Append(Autodesk.Revit.DB.Line.CreateBound(p2, p3));
  profile.Append(Autodesk.Revit.DB.Line.CreateBound(p3, p4)); profile.Append(Autodesk.Revit.DB.Line.CreateBound(p4, p1));
  var arr = new Autodesk.Revit.DB.CurveArrArray(); arr.Append(profile);
  fam.FamilyCreate.NewExtrusion(true, arr, sketchPlane, 2.0);
});
var family = fam.LoadFamily(Document);
System.Console.WriteLine("family-name=" + family.Name + ";");
throw new System.Exception("deliberate, after the load committed");
`, strconv.Quote(template)))
	registerCreatedDocumentCleanup(t, c, instanceID, documentID, rej.Output)
	if rej.Error.Code != "script-execution-failed" {
		t.Fatalf("expected code script-execution-failed, got %q (text: %s)", rej.Error.Code, rej.Text)
	}
	m := regexp.MustCompile(`family-name=([^;]+);`).FindStringSubmatch(rej.Output)
	if m == nil {
		t.Fatalf("the load itself did not succeed before the throw (no family-name marker in output); text: %s", rej.Text)
	}
	familyName := m[1]

	check := runScript(t, c, instanceID, documentID, fmt.Sprintf(`
foreach (Autodesk.Revit.DB.Family f in new Autodesk.Revit.DB.FilteredElementCollector(Document).OfClass(typeof(Autodesk.Revit.DB.Family))) {
  if (f.Name == %s) {
    Connector.WithTransaction(Document, () => { Document.Delete(f.Id); });
    return "still-loaded-then-removed";
  }
}
return "gone";
`, strconv.Quote(familyName)))
	if check.Status != "success" {
		t.Fatalf("expected status=success, got %q (%s)", check.Status, check.diag())
	}
	if !strings.Contains(check.ReturnValue, "gone") {
		t.Fatalf("a family loaded at top level survived a script that threw afterwards -- the group rollback no longer covers self-transacting commits; %s", check.diag())
	}
}
