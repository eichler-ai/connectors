package selfcheck

import (
	"os"
	"path/filepath"
	"testing"
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
