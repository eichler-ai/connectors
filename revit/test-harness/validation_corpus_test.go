//go:build harness

package harness_test

import (
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
// One real discovery-tool GAP found in that same research, worth recording even though it isn't a
// blocker: `Publish`/`ExportsDirectory` (this connector's own script globals, PRD §09) are invisible
// to list_functions/search_functions entirely -- those tools only reflect the RevitAPI corpus, not
// ScriptGlobals itself, so an agent has no discovery path to them at all short of already knowing they
// exist (from get_skills, or from a prior script's own compile error naming `doc` as undefined, as
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
var ok = doc.Export(ExportsDirectory, exportName, views, options);
var dwgPath = System.IO.Path.Combine(ExportsDirectory, exportName + ".dwg");
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
var dir = ExportsDirectory;
foreach (var f in System.IO.Directory.GetFiles(dir, "validation-case01-*")) {
  try { System.IO.File.Delete(f); } catch {}
}
return "cleaned";
`
		_ = callExecuteScriptWith(t, c, instanceID, documentID, cleanup, nil)
	})
}
