//go:build harness

package harness_test

import (
	"encoding/json"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/rhino/test-harness/mcpclient"
)

// frameEnvelope is the structuredContent shape of frame_canvas's result.
type frameEnvelope struct {
	StructuredContent struct {
		Frame *struct {
			GrasshopperDocumentID string    `json:"gh_document_id"`
			Rect                  []float64 `json:"rect"`
			FramedObjectCount     int       `json:"framed_object_count"`
			MatchedComponents     []string  `json:"matched_components"`
			MissingComponents     []string  `json:"missing_components"`
			FramedWholeDefinition bool      `json:"framed_whole_definition"`
		} `json:"frame"`
		Error *notice `json:"error"`
	} `json:"structuredContent"`
}

func callFrame(t *testing.T, c *mcpclient.Client, args map[string]any) frameEnvelope {
	t.Helper()
	raw, err := c.CallTool("frame_canvas", args, 30*time.Second)
	if err != nil {
		t.Fatalf("frame_canvas %v: %v", args, err)
	}
	var env frameEnvelope
	if err := json.Unmarshal(raw, &env); err != nil {
		t.Fatalf("decode frame_canvas: %v\n%s", err, raw)
	}
	return env
}

// TestGrasshopperFrameCanvasLive verifies frame_canvas end to end (PRD §11): it frames the canvas on a named
// component, extends by downstream depth, frames the whole definition when no components are given, and
// surfaces missing components — and a subsequent capture_view target=canvas succeeds. Wiring: slider "r" ->
// "sink"; a far-away panel "p" is unwired.
func TestGrasshopperFrameCanvasLive(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	doc := docID(inst)
	if doc == "" {
		t.Skip("no document open in the connected Rhino")
	}

	open := callExecute(t, c, map[string]any{
		"instance_id": inst.InstanceID, "document_id": doc, "language": "python",
		"script": `import Rhino, System
Rhino.PlugIns.PlugIn.LoadPlugIn(System.Guid("b45a29b1-4343-4035-989e-044e8580d9cf"))
import Grasshopper
server = Grasshopper.Instances.DocumentServer
for existing in list(server):
    server.RemoveDocument(existing)
d = Grasshopper.Kernel.GH_Document()
s = Grasshopper.Kernel.Special.GH_NumberSlider(); s.NickName="r"; s.CreateAttributes()
s.Attributes.Pivot = System.Drawing.PointF(100,100); d.AddObject(s, False)
sink = Grasshopper.Kernel.Parameters.Param_Number(); sink.NickName="sink"; sink.CreateAttributes()
sink.Attributes.Pivot = System.Drawing.PointF(320,100); d.AddObject(sink, False)
sink.AddSource(s)
p = Grasshopper.Kernel.Special.GH_Panel(); p.NickName="p"; p.CreateAttributes()
p.Attributes.Pivot = System.Drawing.PointF(1200,800); d.AddObject(p, False)
d.Enabled = True
try:
    server.AddDocument(d, True)
except TypeError:
    server.AddDocument(d)
Rhino.RhinoApp.RunScript("_Grasshopper", False)
result = "opened active=%s" % (Grasshopper.Instances.ActiveCanvas is not None)`,
	}, 60*time.Second)
	t.Logf("open: status=%s return=%q", open.Status, open.ReturnValue)
	if open.Status != "success" {
		t.Fatalf("could not open Grasshopper: %+v", open.Error)
	}
	time.Sleep(3 * time.Second)

	// Frame just the slider (depth 0): one object, matched, not the whole definition, and a real rect.
	one := callFrame(t, c, map[string]any{"instance_id": inst.InstanceID, "components": []string{"r"}}).StructuredContent
	if one.Frame == nil {
		t.Fatalf("frame_canvas(['r']) returned no frame: %+v", one.Error)
	}
	f := one.Frame
	t.Logf("frame r: objects=%d matched=%v missing=%v whole=%v rect=%v", f.FramedObjectCount, f.MatchedComponents, f.MissingComponents, f.FramedWholeDefinition, f.Rect)
	if f.FramedObjectCount != 1 || len(f.MatchedComponents) != 1 || f.MatchedComponents[0] != "r" {
		t.Errorf("framing just the slider should be 1 matched object, got count=%d matched=%v", f.FramedObjectCount, f.MatchedComponents)
	}
	if f.FramedWholeDefinition {
		t.Errorf("naming a component should not frame the whole definition")
	}
	if len(f.Rect) != 4 || f.Rect[2] <= 0 || f.Rect[3] <= 0 {
		t.Errorf("framed rect should be a real region, got %v", f.Rect)
	}

	// Extend downstream by one level: the slider plus its recipient "sink".
	down := callFrame(t, c, map[string]any{"instance_id": inst.InstanceID, "components": []string{"r"}, "downstream_depth": 1}).StructuredContent
	if down.Frame == nil || down.Frame.FramedObjectCount != 2 {
		t.Errorf("downstream_depth 1 from the slider should include its sink (2 objects), got %+v", down.Frame)
	}

	// No components: the whole definition (slider, sink, panel = 3).
	whole := callFrame(t, c, map[string]any{"instance_id": inst.InstanceID}).StructuredContent
	if whole.Frame == nil || !whole.Frame.FramedWholeDefinition || whole.Frame.FramedObjectCount != 3 {
		t.Errorf("no components should frame the whole 3-object definition, got %+v", whole.Frame)
	}

	// A missing component is surfaced, not an error.
	miss := callFrame(t, c, map[string]any{"instance_id": inst.InstanceID, "components": []string{"nope"}}).StructuredContent
	if miss.Frame == nil || len(miss.Frame.MissingComponents) != 1 || miss.Frame.MissingComponents[0] != "nope" {
		t.Errorf("an unknown component should be reported missing, got %+v", miss.Frame)
	}

	// After framing the slider, a canvas capture succeeds (the region is now framed).
	callFrame(t, c, map[string]any{"instance_id": inst.InstanceID, "components": []string{"r"}})
	env := capture(t, c, map[string]any{"instance_id": inst.InstanceID, "target": "canvas", "format": "png", "width": 700})
	if env.StructuredContent.Error != nil || len(env.StructuredContent.Images) != 1 || env.StructuredContent.Images[0].Viewport != "canvas" {
		t.Fatalf("canvas capture after framing failed: %+v", env.StructuredContent)
	}
	decodeImages(t, env, "gh_frame_capture")
}
