package selfcheck

import (
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestShouldEvict(t *testing.T) {
	cases := []struct {
		own, disk string
		want      bool
	}{
		{"v0.1.2", "v0.1.3", true},  // the live case: installer moved on, this process did not
		{"v0.1.3", "v0.1.2", true},  // a downgrade install should take effect the same way
		{"v0.1.3", "v0.1.3", false}, // current
		{"0.1.3", "v0.1.3", false},  // tag form differences are not differences
		{"V0.1.3", " v0.1.3 ", false},
		{"dev", "v0.1.3", false}, // a dev build is not a release; never evict it
		{"v0.1.3", "dev", false},
		{"v0.1.3", "", false}, // no marker: no opinion (dev checkout, remote-mode Mac broker)
		{"", "v0.1.3", false},
	}
	for _, c := range cases {
		if got := ShouldEvict(c.own, c.disk); got != c.want {
			t.Errorf("ShouldEvict(%q, %q) = %v, want %v", c.own, c.disk, got, c.want)
		}
	}
}

func TestReadMarkerVersion(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "installed-version.json")

	if got := readMarkerVersion(path); got != "" {
		t.Fatalf("missing marker must read as empty, got %q", got)
	}
	os.WriteFile(path, append([]byte{0xEF, 0xBB, 0xBF}, []byte(`{"version":"v0.1.3","deployed":["2027"],"components":{"server":"x"}}`)...), 0o644)
	if got := readMarkerVersion(path); got != "v0.1.3" {
		t.Fatalf("BOM-prefixed marker must parse, got %q", got)
	}
	os.WriteFile(path, []byte("{not json"), 0o644)
	if got := readMarkerVersion(path); got != "" {
		t.Fatalf("corrupt marker must read as empty, got %q", got)
	}
}

func TestPrimaryFailuresGivesUpAfterThreeAgainstOnePid(t *testing.T) {
	var f PrimaryFailures
	if f.Record(100) || f.Record(100) {
		t.Fatal("two failures must not give up")
	}
	if !f.Record(100) {
		t.Fatal("third consecutive failure against the same pid must give up")
	}
}

func TestPrimaryFailuresResetOnNewPidAndOnSuccess(t *testing.T) {
	var f PrimaryFailures
	f.Record(100)
	f.Record(100)
	if f.Record(200) {
		t.Fatal("a different primary pid must start a fresh count")
	}
	f.Record(200)
	f.Reset()
	if f.Record(200) || f.Count() != 1 {
		t.Fatalf("Reset must forget prior failures: count=%d", f.Count())
	}
}

func TestPrimaryFailuresThresholdIsConfigurable(t *testing.T) {
	f := PrimaryFailures{Threshold: 1}
	if !f.Record(7) {
		t.Fatal("threshold 1 must give up on the first failure")
	}
}

func TestExecutableReplacedDetectsAReplacedFileOnly(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "mcp-server.exe")
	os.WriteFile(path, []byte("image-one"), 0o755)
	fi, _ := os.Stat(path)
	stamp := ImageStamp{Path: path, Size: fi.Size(), ModTime: fi.ModTime(), ok: true}

	if ExecutableReplaced(stamp) {
		t.Fatal("unchanged file must not read as replaced")
	}
	// Same size, different content and a later mtime (the installer's Move-Item of a new image).
	os.WriteFile(path, []byte("image-two"), 0o755)
	os.Chtimes(path, fi.ModTime().Add(time.Minute), fi.ModTime().Add(time.Minute))
	if !ExecutableReplaced(stamp) {
		t.Fatal("a replaced file must read as replaced")
	}
	os.Remove(path)
	if ExecutableReplaced(stamp) {
		t.Fatal("a missing file (mid-swap) must not read as replaced")
	}
	if ExecutableReplaced(ImageStamp{}) {
		t.Fatal("an unknown stamp must never evict")
	}
}
