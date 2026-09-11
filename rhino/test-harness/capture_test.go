//go:build harness

package harness_test

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"image"
	_ "image/jpeg"
	_ "image/png"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/rhino/test-harness/mcpclient"
)

type captureEnvelope struct {
	Content []struct {
		Type     string `json:"type"`
		Text     string `json:"text"`
		Data     string `json:"data"`
		MIMEType string `json:"mimeType"`
	} `json:"content"`
	StructuredContent struct {
		Images []struct {
			Viewport string `json:"viewport"`
			Width    int    `json:"width"`
			Height   int    `json:"height"`
		} `json:"images"`
		Notices []notice `json:"notices"`
		Error   *notice  `json:"error"`
	} `json:"structuredContent"`
	IsError bool `json:"isError"`
}

func capture(t *testing.T, c *mcpclient.Client, args map[string]any) captureEnvelope {
	t.Helper()
	raw, err := c.CallTool("capture_view", args, 60*time.Second)
	if err != nil {
		t.Fatalf("capture_view: %v", err)
	}
	var env captureEnvelope
	if err := json.Unmarshal(raw, &env); err != nil {
		t.Fatalf("decode: %v\n%s", err, raw)
	}
	return env
}

// decodeImages returns the decoded PNGs in content order and writes each beside the test output so
// a failing geometry case leaves a picture behind (implementation-plan.md phase 1).
func decodeImages(t *testing.T, env captureEnvelope, label string) [][]byte {
	t.Helper()
	var out [][]byte
	dir := os.Getenv("MCP_HARNESS_CAPTURES")
	for i, c := range env.Content {
		if c.Type != "image" {
			continue
		}
		b, err := base64.StdEncoding.DecodeString(c.Data)
		if err != nil {
			t.Fatalf("image %d is not base64: %v", i, err)
		}
		if _, format, err := image.Decode(bytes.NewReader(b)); err != nil {
			t.Fatalf("image %d is not a decodable image: %v", i, err)
		} else if want := map[string]string{"image/png": "png", "image/jpeg": "jpeg"}[c.MIMEType]; want != format {
			t.Fatalf("image %d declares %s but decodes as %s", i, c.MIMEType, format)
		}
		if dir != "" {
			os.MkdirAll(dir, 0o755)
			ext := "jpg"
			if c.MIMEType == "image/png" {
				ext = "png"
			}
			os.WriteFile(filepath.Join(dir, fmt.Sprintf("%s-%d.%s", label, i, ext)), b, 0o644)
		}
		out = append(out, b)
	}
	return out
}

// captureForDiagnostics is the helper every geometry case can call on failure.
func captureForDiagnostics(t *testing.T, c *mcpclient.Client, inst instance, label string) {
	t.Helper()
	env := capture(t, c, map[string]any{"instance_id": inst.InstanceID, "target": "active", "zoom": "extents"})
	if env.IsError {
		t.Logf("diagnostic capture failed: %+v", env.StructuredContent.Error)
		return
	}
	imgs := decodeImages(t, env, label)
	t.Logf("diagnostic capture: %d image(s) (set MCP_HARNESS_CAPTURES to keep them)", len(imgs))
}

func TestCaptureViewReturnsADecodableImage(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	// Put something in the scene so the capture is not blank, then capture with zoom extents.
	csharp(t, c, inst, `Document.Objects.AddSphere(new Rhino.Geometry.Sphere(Rhino.Geometry.Point3d.Origin, 10)); return 1;`, nil)
	env := capture(t, c, map[string]any{"instance_id": inst.InstanceID, "target": "active", "zoom": "extents", "display_mode": "Shaded"})
	if env.IsError {
		t.Fatalf("%+v", env.StructuredContent.Error)
	}
	imgs := decodeImages(t, env, "active")
	if len(imgs) != 1 {
		t.Fatalf("expected 1 image, got %d", len(imgs))
	}
	meta := env.StructuredContent.Images[0]
	// The size budget the review asked for: a default capture must stay well inside the client's
	// output ceiling (base64 inflates by 4/3; keep the raw bytes under 300 KB).
	if len(imgs[0]) > 300*1024 {
		t.Fatalf("default capture is %d bytes; too large for one inline result", len(imgs[0]))
	}
	img, _, _ := image.Decode(bytes.NewReader(imgs[0]))
	b := img.Bounds()
	if b.Dx() != meta.Width || b.Dy() != meta.Height {
		t.Fatalf("metadata %dx%d != png %dx%d", meta.Width, meta.Height, b.Dx(), b.Dy())
	}
	if max := b.Dx(); b.Dy() > max {
		max = b.Dy()
	} else if max > 2048 || max < 64 {
		t.Fatalf("long edge %d out of bounds", max)
	}
	// Not blank: at least two distinct colours.
	seen := map[uint32]bool{}
	for y := 0; y < b.Dy(); y += 7 {
		for x := 0; x < b.Dx(); x += 7 {
			r, g, bb, _ := img.At(x, y).RGBA()
			seen[r<<16|g<<8|bb] = true
			if len(seen) > 1 {
				break
			}
		}
	}
	if len(seen) < 2 {
		t.Fatal("the capture is a single flat colour")
	}
	t.Logf("captured %s at %dx%d, %d bytes", meta.Viewport, meta.Width, meta.Height, len(imgs[0]))
}

func TestCaptureAllViewports(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	env := capture(t, c, map[string]any{"instance_id": inst.InstanceID, "target": "all", "width": 400})
	if env.IsError {
		t.Fatalf("%+v", env.StructuredContent.Error)
	}
	imgs := decodeImages(t, env, "all")
	if len(imgs) < 2 || len(imgs) != len(env.StructuredContent.Images) {
		t.Fatalf("expected one image per viewport, got %d images for %d viewports", len(imgs), len(env.StructuredContent.Images))
	}
	for _, m := range env.StructuredContent.Images {
		if m.Width != 400 {
			t.Fatalf("width not honoured: %+v", m)
		}
	}
}

func TestCapturePngWhenTransparent(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	env := capture(t, c, map[string]any{"instance_id": inst.InstanceID, "transparent_background": true, "width": 320})
	if env.IsError {
		t.Fatalf("%+v", env.StructuredContent.Error)
	}
	decodeImages(t, env, "transparent")
	if env.StructuredContent.Images[0].Width != 320 {
		t.Fatalf("%+v", env.StructuredContent.Images[0])
	}
	for _, cc := range env.Content {
		if cc.Type == "image" && cc.MIMEType != "image/png" {
			t.Fatalf("transparent capture must be png, got %s", cc.MIMEType)
		}
	}
}

func TestCaptureUnknownViewport_ListsTheKnownOnes(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	env := capture(t, c, map[string]any{"instance_id": inst.InstanceID, "target": "NoSuchView"})
	if !env.IsError || env.StructuredContent.Error == nil || env.StructuredContent.Error.Code != "viewport-not-found" {
		t.Fatalf("%+v", env.StructuredContent)
	}
}

func TestCaptureWhileAScriptRuns_IsBusy(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	long := csharp(t, c, inst, `var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < 2500) { CancellationToken.ThrowIfCancellationRequested(); System.Threading.Thread.Sleep(50); } return 1;`, map[string]any{"timeout_ms": 200})
	env := capture(t, c, map[string]any{"instance_id": inst.InstanceID})
	if !env.IsError || env.StructuredContent.Error == nil || env.StructuredContent.Error.Code != "instance-busy" {
		t.Fatalf("%+v", env.StructuredContent)
	}
	poll(t, c, long.ExecutionID, 10000)
	// The script's result is pollable a moment before the plug-in's main-thread executor releases and
	// the instance reports idle again; on the slower Windows host that gap let the next shared-instance
	// cases see this run's leftover busy state. Drain to idle before returning.
	waitForIdle(t, c, inst.InstanceID)
}
