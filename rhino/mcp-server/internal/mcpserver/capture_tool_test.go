package mcpserver

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/execution"
)

func TestCaptureFilePaths(t *testing.T) {
	// No file_path -> no on-disk targets.
	if paths, drec := captureFilePaths("", []execution.CapturedImage{{Viewport: "Perspective"}}); paths != nil || drec != nil {
		t.Fatalf("empty file_path should yield no paths: %v %v", paths, drec)
	}

	// One image -> the path verbatim.
	paths, drec := captureFilePaths("/tmp/shot.png", []execution.CapturedImage{{Viewport: "Perspective", MIMEType: "image/png"}})
	if drec != nil {
		t.Fatal(drec)
	}
	if len(paths) != 1 || paths[0] != "/tmp/shot.png" {
		t.Fatalf("single image path = %v", paths)
	}

	// Several images -> viewport spliced before the extension, sanitised.
	imgs := []execution.CapturedImage{
		{Viewport: "Perspective", MIMEType: "image/png"},
		{Viewport: "Top/Plan", MIMEType: "image/png"},
	}
	paths, drec = captureFilePaths("/tmp/shot.png", imgs)
	if drec != nil {
		t.Fatal(drec)
	}
	want := []string{"/tmp/shot-Perspective.png", "/tmp/shot-Top_Plan.png"}
	for i := range want {
		if paths[i] != want[i] {
			t.Fatalf("multi path[%d] = %q, want %q", i, paths[i], want[i])
		}
	}

	// A leading ~ expands to the home directory.
	home, err := os.UserHomeDir()
	if err == nil {
		paths, _ = captureFilePaths("~/shots/x.jpg", []execution.CapturedImage{{Viewport: "Perspective"}})
		if paths[0] != filepath.Join(home, "shots", "x.jpg") {
			t.Fatalf("~ not expanded: %q", paths[0])
		}
	}
}

func TestWriteCaptureFileCreatesParents(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "nested", "deeper", "shot.png")
	data := []byte{0x89, 'P', 'N', 'G'}
	if err := writeCaptureFile(path, data); err != nil {
		t.Fatalf("write failed: %v", err)
	}
	got, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read back failed: %v", err)
	}
	if string(got) != string(data) {
		t.Fatalf("round-trip mismatch: %v", got)
	}
}

func TestFormatFromExt(t *testing.T) {
	for path, want := range map[string]string{
		"/tmp/shot.png": "png", "/tmp/shot.PNG": "png",
		"/tmp/shot.jpg": "jpeg", "/tmp/shot.jpeg": "jpeg",
		"/tmp/shot.gif": "", "/tmp/shot": "", "": "",
	} {
		if got := formatFromExt(path); got != want {
			t.Errorf("formatFromExt(%q) = %q, want %q", path, got, want)
		}
	}
}

func TestExtAndViewportNameHelpers(t *testing.T) {
	if extForMIME("image/png") != ".png" || extForMIME("image/jpeg") != ".jpg" || extForMIME("x") != "" {
		t.Fatal("extForMIME wrong")
	}
	if safeViewportName("Top / Plan") != "Top___Plan" {
		t.Fatalf("safeViewportName = %q", safeViewportName("Top / Plan"))
	}
	if safeViewportName("") != "viewport" {
		t.Fatal("empty viewport name should fall back")
	}
}
