//go:build harness

package harness_test

import (
	"bytes"
	"image"
	_ "image/jpeg"
	_ "image/png"
	"strings"
	"testing"
	"time"
)

// TestGrasshopperCanvasCaptureLive verifies capture_view's "canvas" target end to end (PRD §11): with the
// Grasshopper editor open on a definition, capture_view target=canvas returns a real image of the canvas.
// It opens Grasshopper itself and builds a small definition, so it needs no pre-opened .gh.
func TestGrasshopperCanvasCaptureLive(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	doc := docID(inst)
	if doc == "" {
		t.Skip("no document open in the connected Rhino; open one so the load-Grasshopper script can run")
	}

	// Run 1: load Grasshopper, build a small definition, and open the editor window. The window is created
	// by the message loop only AFTER this run returns, so the canvas is used from a later call.
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
p = Grasshopper.Kernel.Special.GH_Panel(); p.NickName="p"; p.CreateAttributes()
p.Attributes.Pivot = System.Drawing.PointF(400,120); d.AddObject(p, False)
d.Enabled = True
try:
    server.AddDocument(d, True)
except TypeError:
    server.AddDocument(d)
Rhino.RhinoApp.RunScript("_Grasshopper", False)
result = "opened active_canvas=%s" % (Grasshopper.Instances.ActiveCanvas is not None)`,
	}, 60*time.Second)
	t.Logf("open grasshopper: status=%s return=%q", open.Status, open.ReturnValue)
	if open.Status != "success" {
		t.Fatalf("could not open Grasshopper: %+v", open.Error)
	}

	// Let the message loop create the editor window, then make our definition the active canvas document so
	// the capture has content (the editor may open on a different/empty document).
	time.Sleep(3 * time.Second)
	setup := callExecute(t, c, map[string]any{
		"instance_id": inst.InstanceID, "document_id": doc, "language": "python",
		"script": `import Grasshopper
canvas = Grasshopper.Instances.ActiveCanvas
docs = list(Grasshopper.Instances.DocumentServer)
if canvas is not None and docs:
    canvas.Document = docs[0]
result = "canvas=%s doc_set=%s" % (canvas is not None, canvas is not None and canvas.Document is not None)`,
	}, 30*time.Second)
	t.Logf("canvas setup: status=%s return=%q", setup.Status, setup.ReturnValue)
	if setup.Status != "success" || !strings.Contains(setup.ReturnValue, "canvas=True") {
		t.Skipf("the Grasshopper editor canvas is not available in this session (return=%q); canvas capture needs the editor open", setup.ReturnValue)
	}

	// Capture the canvas.
	env := capture(t, c, map[string]any{"instance_id": inst.InstanceID, "target": "canvas", "format": "png", "width": 800})
	if env.StructuredContent.Error != nil {
		t.Fatalf("canvas capture failed: %+v", env.StructuredContent.Error)
	}
	if len(env.StructuredContent.Images) != 1 {
		t.Fatalf("expected exactly one canvas image, got %d", len(env.StructuredContent.Images))
	}
	meta := env.StructuredContent.Images[0]
	t.Logf("canvas image: viewport=%q %dx%d", meta.Viewport, meta.Width, meta.Height)
	if meta.Viewport != "canvas" {
		t.Errorf("the canvas image should be labelled viewport \"canvas\", got %q", meta.Viewport)
	}
	if meta.Width != 800 {
		t.Errorf("requested width 800, got %d", meta.Width)
	}
	// The bytes must be a real, decodable PNG whose pixels match the reported size (not a 1px/blank render).
	imgs := decodeImages(t, env, "gh_canvas")
	if len(imgs) != 1 {
		t.Fatalf("expected one decodable image, got %d", len(imgs))
	}
	cfg, _, err := image.DecodeConfig(bytes.NewReader(imgs[0]))
	if err != nil {
		t.Fatalf("canvas image did not decode: %v", err)
	}
	if cfg.Width != meta.Width || cfg.Height != meta.Height {
		t.Errorf("decoded image %dx%d disagrees with the reported %dx%d", cfg.Width, cfg.Height, meta.Width, meta.Height)
	}
	if cfg.Width < 64 || cfg.Height < 64 {
		t.Errorf("canvas image is degenerate (%dx%d)", cfg.Width, cfg.Height)
	}

	// The default-size JPEG path (no width/height, default format) also renders a valid canvas image.
	jpg := capture(t, c, map[string]any{"instance_id": inst.InstanceID, "target": "canvas"})
	if jpg.StructuredContent.Error != nil || len(jpg.StructuredContent.Images) != 1 {
		t.Fatalf("default-size canvas capture failed: %+v", jpg.StructuredContent.Error)
	}
	jm := jpg.StructuredContent.Images[0]
	t.Logf("default canvas image: viewport=%q %dx%d", jm.Viewport, jm.Width, jm.Height)
	if jm.Viewport != "canvas" || jm.Width <= 0 || jm.Height <= 0 {
		t.Errorf("default canvas capture returned a bad image: %+v", jm)
	}
}
