package howto

import (
	"bytes"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// The embedded corpus is a build artefact the repo validates: every file
// parses and validates, is named for its id, and every sidecar stamp still
// binds to a current script (a stamp whose hash no longer matches is stale
// and must be pruned by the sweep that changed the script -- CI fails here
// rather than shipping a stamp for a script nobody ran).
func TestEmbeddedCorpusIsValidAndStampsAreCurrent(t *testing.T) {
	c, stamps, ver, err := Embedded()
	if err != nil {
		t.Fatal(err)
	}
	if c.Len() == 0 || ver.Documents != c.Len() || len(ver.Hash) != 12 {
		t.Fatalf("version = %+v for %d documents", ver, c.Len())
	}
	raw, err := os.ReadFile(filepath.Join("corpus", "verified.jsonl"))
	if err != nil {
		t.Fatal(err)
	}
	if len(bytes.TrimSpace(raw)) > 0 {
		sc, err := LoadSidecar(bytes.NewReader(raw))
		if err != nil {
			t.Fatal(err)
		}
		if _, dropped := sc.Prune(c); dropped > 0 {
			t.Fatalf("%d stale stamp(s) in %s/verified.jsonl: their script hash or revision no longer matches the document; rerun the sweep (go test -tags harness -run TestHowToSweep -howto-stamps) or delete them", dropped, CorpusDir)
		}
		if len(stamps) != len(sc.Stamps) {
			t.Fatalf("embedded kept %d of %d stamps", len(stamps), len(sc.Stamps))
		}
	}
	// A doc-level pitfall or step comment must not cite the pre-#167 skill.md
	// section titles, which no longer exist.
	for _, id := range c.IDs() {
		d, _, _ := c.Get(id)
		text := d.Script + "\n" + d.Task + "\n" + d.Title
		for _, p := range d.Pitfalls {
			text += "\n" + p.Symptom + "\n" + p.Cause + "\n" + p.Fix
		}
		for _, old := range []string{"Writing: one block per batch", "What you may not do", "Calls that need their target not modifiable"} {
			if strings.Contains(text, old) {
				t.Errorf("%s cites the removed skill.md section %q", id, old)
			}
		}
		if d.Provenance.Kind == "" {
			t.Errorf("%s: provenance.kind missing", id)
		}
	}
	if !strings.Contains(ver.String(), "how-to corpus:") {
		t.Fatalf("version string: %q", ver.String())
	}
}
