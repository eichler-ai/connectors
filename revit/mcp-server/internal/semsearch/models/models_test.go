package models

import (
	"os"
	"path/filepath"
	"testing"
)

// The models are fetched at build time, so this package has two legitimate
// states. Without them: Available is false and every accessor errors with a
// message naming the fetch step. With them: pins verify and Materialize
// produces a loadable directory. Both are asserted, whichever state the
// build is in, so a stale pin or a broken fetch fails here first.
func TestEmbeddedStateIsConsistent(t *testing.T) {
	if !Available() {
		t.Logf("models not fetched (missing %v); asserting the degraded path", Missing())
		if _, _, _, err := Embedder(); err == nil {
			t.Fatal("Embedder() succeeded with models missing")
		}
		if _, err := Materialize(t.TempDir()); err == nil {
			t.Fatal("Materialize() succeeded with models missing")
		}
		if err := Verify(); err == nil {
			t.Fatal("Verify() succeeded with models missing")
		}
		return
	}
	if len(Missing()) != 0 {
		t.Fatalf("Available() but Missing() = %v", Missing())
	}
	if err := Verify(); err != nil {
		t.Fatal(err)
	}
	tok, st, normalize, err := Embedder()
	if err != nil || len(tok) == 0 || len(st) == 0 || !normalize {
		t.Fatalf("Embedder(): %v (%d, %d bytes, normalize=%v)", err, len(tok), len(st), normalize)
	}
	dir, err := Materialize(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	for _, f := range []string{"model.onnx", "tokenizer.json", "config.json", "special_tokens_map.json"} {
		if _, err := os.Stat(filepath.Join(dir, f)); err != nil {
			t.Errorf("materialized dir lacks %s: %v", f, err)
		}
	}
	// Second call is a no-op on already-present files (no error, same dir).
	if again, err := Materialize(filepath.Dir(dir)); err != nil || again != dir {
		t.Fatalf("re-Materialize: %s %v", again, err)
	}
}
