package mcpserver

import (
	"reflect"
	"testing"

	"github.com/eichler-ai/connectors/internal/servercore/diag"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/execution"
)

func TestDoFrame_RequiresInstanceID(t *testing.T) {
	called := false
	out, isErr := doFrame(FrameCanvasIn{}, func(string, []string, int, int, float64) (*execution.FrameResult, *diag.Record) {
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

func TestDoFrame_NilPaddingBecomesBridgeDefaultSentinel(t *testing.T) {
	var gotPadding float64
	var gotComponents []string
	want := &execution.FrameResult{GrasshopperDocumentID: "gh-1", FramedWholeDefinition: true}
	out, isErr := doFrame(
		FrameCanvasIn{InstanceID: "i1", Components: []string{"a", "b"}, UpstreamDepth: 2},
		func(gh string, components []string, up, down int, padding float64) (*execution.FrameResult, *diag.Record) {
			gotComponents, gotPadding = components, padding
			return want, nil
		})
	if isErr || out.Frame != want {
		t.Fatalf("expected the frame result passed through, got isErr=%v frame=%#v", isErr, out.Frame)
	}
	if gotPadding != -1 {
		t.Fatalf("a nil padding should pass -1 (use the bridge default), got %v", gotPadding)
	}
	if !reflect.DeepEqual(gotComponents, []string{"a", "b"}) {
		t.Fatalf("components not forwarded: %#v", gotComponents)
	}
}

func TestDoFrame_ExplicitZeroPaddingIsForwarded(t *testing.T) {
	zero := 0.0
	var gotPadding float64 = -99
	doFrame(FrameCanvasIn{InstanceID: "i1", Padding: &zero},
		func(_ string, _ []string, _, _ int, padding float64) (*execution.FrameResult, *diag.Record) {
			gotPadding = padding
			return &execution.FrameResult{}, nil
		})
	if gotPadding != 0 {
		t.Fatalf("an explicit padding of 0 should be forwarded as 0, got %v", gotPadding)
	}
}

func TestDoFrame_PropagatesBridgeError(t *testing.T) {
	drec := diag.New(diag.SeverityError, "grasshopper-canvas-unavailable", "test", "no canvas")
	out, isErr := doFrame(FrameCanvasIn{InstanceID: "i1"},
		func(string, []string, int, int, float64) (*execution.FrameResult, *diag.Record) { return nil, drec })
	if !isErr || out.Error != drec || out.Frame != nil {
		t.Fatalf("a bridge diagnostic should surface as the tool error, got isErr=%v err=%#v", isErr, out.Error)
	}
}
