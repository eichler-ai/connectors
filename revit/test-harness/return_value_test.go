//go:build harness

package harness_test

import (
	"strings"
	"testing"
)

// TestReturnValueSerialization pins issue #117 against a real Revit instance:
// the reported script, the reported symptom, and the field split that keeps
// Revit's own console writes out of the answer.
//
// Tier 1 covers the formatting rules in isolation (ReturnValueFormatterTests)
// and the wire shape (ExecutionResultMessageTests / RequestDispatcherTests).
// What only a live run can show is the pairing that produced the bug report:
// a FilteredElementCollector query, projected through an anonymous type, over
// a document Revit is actually chattering about on the process console.
func TestReturnValueSerialization(t *testing.T) {
	c, instanceID, documentID := targetDocument(t)

	// The script from the issue, verbatim in shape: collect levels, project
	// each to an anonymous type, return the list. Before the fix this came
	// back as "System.Collections.Generic.List`1[<>f__AnonymousType0#1[...]]".
	t.Run("ListOfAnonymousProjectionsComesBackAsData", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, `
var levels = System.Linq.Enumerable.ToList(
    System.Linq.Enumerable.Select(
        System.Linq.Enumerable.Cast<Autodesk.Revit.DB.Level>(
            new Autodesk.Revit.DB.FilteredElementCollector(Document).OfClass(typeof(Autodesk.Revit.DB.Level))),
        l => new { l.Name, l.Elevation }));
return levels;
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		if strings.Contains(out.ReturnValue, "AnonymousType") || strings.Contains(out.ReturnValue, "System.Collections.Generic.List") {
			t.Fatalf("the return value is still a type name rather than data: %s", out.diag())
		}
		// Every Revit project template ships at least one level, so this is a
		// real array with real members, not an empty one that would pass the
		// check above for the wrong reason.
		if !strings.HasPrefix(strings.TrimSpace(out.ReturnValue), "[{") || !strings.Contains(out.ReturnValue, `"Name":`) || !strings.Contains(out.ReturnValue, `"Elevation":`) {
			t.Fatalf("expected a JSON array of {Name, Elevation} objects; got: %s", out.diag())
		}
	})

	// The honest-fallback half: a Revit Element does not override ToString(),
	// so it used to render as its own type name with nothing marking it as a
	// non-answer. It is deliberately still not serialized -- walking a live
	// Element's properties on the UI thread is exactly what the formatter
	// refuses to do -- but it must SAY so.
	t.Run("ARevitElementSaysItHasNoDisplayForm", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, `
return System.Linq.Enumerable.First(
    System.Linq.Enumerable.Cast<Autodesk.Revit.DB.Level>(
        new Autodesk.Revit.DB.FilteredElementCollector(Document).OfClass(typeof(Autodesk.Revit.DB.Level))));
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		if !strings.Contains(out.ReturnValue, "no display form") || !strings.Contains(out.ReturnValue, "Autodesk.Revit.DB.Level") {
			t.Fatalf("a returned Element must report that it has no display form and name its type; got: %s", out.diag())
		}
	})

	// Field separation, live. Anything Revit itself writes to the console
	// during the run lands in output too, which is the whole point: whatever
	// is in output, return_value stays exactly what the script returned.
	t.Run("StdOutAndReturnValueAreSeparateFields", func(t *testing.T) {
		out := runScript(t, c, instanceID, documentID, `
System.Console.WriteLine("harness-stdout-marker");
return "harness-return-marker";
`)
		if out.Status != "success" {
			t.Fatalf("expected status=success, got %q (%s)", out.Status, out.diag())
		}
		if out.ReturnValue != "harness-return-marker" {
			t.Errorf("%s, want exactly the returned string with nothing else folded in", out.diag())
		}
		if !strings.Contains(out.Output, "harness-stdout-marker") {
			t.Errorf("output = %q, want the captured stdout", out.Output)
		}
	})
}
