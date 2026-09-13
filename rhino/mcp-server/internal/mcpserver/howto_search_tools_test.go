package mcpserver

import (
	"context"
	"testing"

	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/howtosearch"
)

func TestResolveHowToVersion(t *testing.T) {
	var deps HowToDeps // nil router: explicit rhino_version path only

	if _, drec := resolveHowToVersion(deps, "", ""); drec == nil || drec.Code != "howto-version-required" {
		t.Errorf("neither given should be howto-version-required, got %+v", drec)
	}
	if _, drec := resolveHowToVersion(deps, "inst-1", "8"); drec == nil || drec.Code != "howto-version-required" {
		t.Errorf("both given should be howto-version-required, got %+v", drec)
	}
	if _, drec := resolveHowToVersion(deps, "", "banana"); drec == nil || drec.Code != "howto-version-invalid" {
		t.Errorf("a non-version should be howto-version-invalid, got %+v", drec)
	}
	// A reported service release normalizes to the major version.
	if ver, drec := resolveHowToVersion(deps, "", "8.35"); drec != nil || ver != "8" {
		t.Errorf("8.35 should resolve to major 8, got ver=%q drec=%+v", ver, drec)
	}
	if ver, drec := resolveHowToVersion(deps, "", "8"); drec != nil || ver != "8" {
		t.Errorf("8 should resolve to 8, got ver=%q drec=%+v", ver, drec)
	}
	// instance_id with a nil router cannot resolve.
	if _, drec := resolveHowToVersion(deps, "inst-1", ""); drec == nil || drec.Code != "no-instance-connected" {
		t.Errorf("instance_id with no router should be no-instance-connected, got %+v", drec)
	}
}

func TestVersionLessIsNumericNotLexical(t *testing.T) {
	// The bug the Revit copy carried: "10" < "8" lexically. Rhino majors are
	// 1-2 digits, so the comparison must be numeric.
	if !versionLess("8", "10") {
		t.Error(`8 should be less than 10 (numeric), lexical comparison gets this wrong`)
	}
	if versionLess("10", "8") {
		t.Error("10 is not less than 8")
	}
	if versionLess("8", "8") {
		t.Error("8 is not less than 8")
	}
	// An unparseable value never fabricates a boundary (returns false both ways).
	if versionLess("x", "8") || versionLess("8", "x") {
		t.Error("an unparseable version must not fabricate an ordering")
	}
}

func TestSearchHowTosOverEmbeddedCorpus(t *testing.T) {
	deps := HowToDeps{Search: howtosearch.New(nil, nil, nil)}
	out := searchHowTos(context.Background(), deps, SearchHowTosIn{Query: "create a layer and draw a circle on it", RhinoVersion: "8"})
	if out.Error != nil {
		t.Fatalf("search errored: %+v", out.Error)
	}
	if out.RhinoVersion != "8" {
		t.Errorf("resolved version should echo as 8, got %q", out.RhinoVersion)
	}
	if len(out.Results) == 0 {
		t.Fatal("expected results for a seed task")
	}
	for _, r := range out.Results {
		if r.VerifiedHere {
			t.Errorf("%s: nothing is verified yet (no stamps ship), got verified_here=true", r.ID)
		}
		if r.Source != "seed" {
			t.Errorf("%s: source should be seed, got %q", r.ID, r.Source)
		}
	}
}

func TestSearchHowTosRequiresVersionAndQuery(t *testing.T) {
	deps := HowToDeps{Search: howtosearch.New(nil, nil, nil)}
	if out := searchHowTos(context.Background(), deps, SearchHowTosIn{Query: "x"}); out.Error == nil {
		t.Error("a search with no version should error")
	}
	if out := searchHowTos(context.Background(), deps, SearchHowTosIn{RhinoVersion: "8"}); out.Error == nil || out.Error.Code != "invalid-params" {
		t.Errorf("an empty query should be invalid-params, got %+v", out.Error)
	}
}

func TestDescribeHowTo(t *testing.T) {
	deps := HowToDeps{Search: howtosearch.New(nil, nil, nil)}
	out := describeHowTo(context.Background(), deps, DescribeHowToIn{ID: "add-a-sphere-to-the-document", RhinoVersion: "8"})
	if out.Error != nil {
		t.Fatalf("describe errored: %+v", out.Error)
	}
	if out.Document == nil || out.Document.Script == "" {
		t.Fatal("describe should return the full document with its script")
	}
	if out.Document.ScriptLang != "python" {
		t.Errorf("script_language should be python, got %q", out.Document.ScriptLang)
	}
	if out.Verification != nil {
		t.Error("no stamp ships yet, so verification should be nil")
	}
	// A missing id is a clean not-found, not a crash.
	if miss := describeHowTo(context.Background(), deps, DescribeHowToIn{ID: "nope", RhinoVersion: "8"}); miss.Error == nil || miss.Error.Code != "howto-not-found" {
		t.Errorf("a missing id should be howto-not-found, got %+v", miss.Error)
	}
}
