package mcpserver

import (
	"testing"

	"github.com/eichler-ai/connectors/internal/servercore/diag"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/execution"
)

func TestDoInspect_RequiresInstanceID(t *testing.T) {
	called := false
	out, isErr := doInspect(InspectDefinitionIn{}, func(string, string, int, int) (*execution.InspectResult, *diag.Record) {
		called = true
		return nil, nil
	})
	if !isErr || out.Error == nil || out.Error.Code != "invalid-param" {
		t.Fatalf("missing instance_id should error invalid-param, got isErr=%v err=%#v", isErr, out.Error)
	}
	if called {
		t.Fatal("the bridge must not be called when instance_id is missing")
	}
}

func TestDoInspect_PassesParamsThroughAndReturnsDefinition(t *testing.T) {
	want := &execution.InspectResult{GrasshopperDocumentID: "gh-1", ObjectCount: 3,
		Objects: []execution.InspectObject{{GUID: "g1", Nickname: "Radius", Kind: "param"}}}
	var gotGH, gotFilter string
	var gotOffset, gotLimit int
	out, isErr := doInspect(
		InspectDefinitionIn{InstanceID: "inst-1", GrasshopperDocumentID: "gh-1", NameFilter: "rad", Offset: 10, Limit: 50},
		func(gh, filter string, offset, limit int) (*execution.InspectResult, *diag.Record) {
			gotGH, gotFilter, gotOffset, gotLimit = gh, filter, offset, limit
			return want, nil
		})
	if isErr || out.Definition != want {
		t.Fatalf("expected the definition passed through, got isErr=%v def=%#v", isErr, out.Definition)
	}
	if gotGH != "gh-1" || gotFilter != "rad" || gotOffset != 10 || gotLimit != 50 {
		t.Fatalf("params not forwarded: gh=%q filter=%q offset=%d limit=%d", gotGH, gotFilter, gotOffset, gotLimit)
	}
}

func TestDoInspect_PropagatesBridgeError(t *testing.T) {
	drec := diag.New(diag.SeverityError, "grasshopper-definition-not-found", "test", "no such definition")
	out, isErr := doInspect(InspectDefinitionIn{InstanceID: "inst-1"},
		func(string, string, int, int) (*execution.InspectResult, *diag.Record) { return nil, drec })
	if !isErr || out.Error != drec || out.Definition != nil {
		t.Fatalf("a bridge diagnostic should surface as the tool error, got isErr=%v err=%#v def=%#v", isErr, out.Error, out.Definition)
	}
}
