package howto

import (
	"bytes"
	"strings"
	"testing"
	"time"
)

// validDoc builds a minimal schema-valid howto Document for marshalling into
// corpus lines in tests.
func validDoc(id, script string) *Document {
	ts := time.Date(2026, 9, 12, 0, 0, 0, 0, time.UTC)
	return &Document{
		SchemaVersion: 1,
		ID:            id,
		Rev:           1,
		Kind:          KindHowTo,
		Title:         "A test how-to title",
		Task:          "Do a concrete Rhino thing that is at least twenty chars long.",
		Members:       []string{"Rhino.Geometry.Sphere.#ctor"},
		Script:        script,
		ScriptLang:    ScriptPython,
		Provenance:    Provenance{Kind: ProvenanceMaintainer},
		CreatedAt:     ts,
		UpdatedAt:     ts,
	}
}

func lines(t *testing.T, docs ...*Document) string {
	t.Helper()
	var b strings.Builder
	for _, d := range docs {
		raw, err := MarshalDocument(d)
		if err != nil {
			t.Fatalf("marshal %s: %v", d.ID, err)
		}
		b.Write(raw)
		b.WriteByte('\n')
	}
	return b.String()
}

func TestEmbeddedCorpusIsValid(t *testing.T) {
	c, stamps, ver, err := Embedded()
	if err != nil {
		t.Fatalf("embedded corpus did not load: %v", err)
	}
	if c.Len() < 3 {
		t.Fatalf("expected the seed corpus to hold the seed how-tos, got %d", c.Len())
	}
	// Every shipped how-to carries a harness stamp (written by the sweep), all on Rhino 8.
	if len(stamps) != c.Len() {
		t.Errorf("every seed how-to should ship a harness stamp: %d docs, %d stamps", c.Len(), len(stamps))
	}
	if len(ver.VerifiedOn) != 1 || ver.VerifiedOn[0] != "8" {
		t.Errorf("seed corpus is verified on Rhino 8, got %v", ver.VerifiedOn)
	}
	if ver.Documents != c.Len() || ver.Hash == "" {
		t.Errorf("version looks wrong: %+v", ver)
	}
	for _, id := range c.IDs() {
		d, _, _ := c.Get(id)
		if d.Kind != KindHowTo {
			t.Errorf("%s: kind=%q, want howto", id, d.Kind)
		}
		if d.ScriptLang != ScriptPython || strings.TrimSpace(d.Script) == "" {
			t.Errorf("%s: seed docs are python with a script, got lang=%q empty=%v", id, d.ScriptLang, d.Script == "")
		}
		if d.Verify == nil || d.Verify.ExpectObjectDelta == nil {
			t.Errorf("%s: seed docs declare an expect_object_delta for the sweep", id)
		}
		// Every seed doc has a CURRENT passing stamp for Rhino 8 (hash-bound).
		v := VerifiedOn(d, stamps)
		if st, ok := v.ByVersion["8"]; !ok || st.Status != StampPassed || st.By != ByHarness {
			t.Errorf("%s: expected a current passing harness stamp for Rhino 8, got %+v", id, v.ByVersion)
		}
	}
}

func TestLoadCorpusRejectsDuplicateID(t *testing.T) {
	jsonl := lines(t, validDoc("dup-id", "result = 1"), validDoc("dup-id", "result = 2"))
	if _, err := LoadCorpus(bytes.NewReader([]byte(jsonl)), SourceSeed); err == nil {
		t.Fatal("expected a duplicate-id error")
	}
}

func TestLoadCorpusSkipsInvalidLineButKeepsRest(t *testing.T) {
	jsonl := lines(t, validDoc("good-one", "result = 1")) + "{\"not\":\"a valid document\"}\n" + lines(t, validDoc("good-two", "result = 2"))
	c, err := LoadCorpus(bytes.NewReader([]byte(jsonl)), SourceSeed)
	if err != nil {
		t.Fatalf("a single invalid line should be skipped, not fatal: %v", err)
	}
	if c.Len() != 2 {
		t.Errorf("want 2 valid docs kept, got %d", c.Len())
	}
	if c.Skipped != 1 || len(c.Problems) != 1 {
		t.Errorf("want 1 skipped line reported, got skipped=%d problems=%d", c.Skipped, len(c.Problems))
	}
}

func TestStampMatchesBindsToRevAndScript(t *testing.T) {
	d := validDoc("bind-me", "result = 42")
	good := Stamp{ID: "bind-me", Rev: 1, ScriptSHA256: ScriptSHA256("result = 42"), RhinoVersion: "8", Status: StampPassed, By: ByHarness}
	if !good.Matches(d) {
		t.Fatal("a stamp for the exact id/rev/script should match")
	}
	staleScript := good
	staleScript.ScriptSHA256 = ScriptSHA256("result = 43")
	if staleScript.Matches(d) {
		t.Error("a stamp whose script hash differs is stale")
	}
	staleRev := good
	staleRev.Rev = 2
	if staleRev.Matches(d) {
		t.Error("a stamp for another revision is stale")
	}
}

func TestVerifiedOnClassifiesAndPrunes(t *testing.T) {
	d := validDoc("verify-me", "result = 1")
	sha := ScriptSHA256(d.Script)
	stamps := []Stamp{
		{ID: "verify-me", Rev: 1, ScriptSHA256: sha, RhinoVersion: "8", Status: StampPassed, By: ByHarness},
		{ID: "verify-me", Rev: 1, ScriptSHA256: sha, RhinoVersion: "7", Status: StampFailed, By: ByHarness, Diagnostic: "broke"},
		// stale: script changed
		{ID: "verify-me", Rev: 1, ScriptSHA256: ScriptSHA256("other"), RhinoVersion: "6", Status: StampPassed, By: ByHarness},
	}
	v := VerifiedOn(d, stamps)
	if len(v.Passed) != 1 || v.Passed[0] != "8" {
		t.Errorf("passed should be [8], got %v", v.Passed)
	}
	if len(v.Failed) != 1 || v.Failed[0] != "7" {
		t.Errorf("failed should be [7], got %v", v.Failed)
	}
	if _, ok := v.ByVersion["6"]; ok {
		t.Error("the stale (changed-script) stamp must not count")
	}
}

func TestHarnessStampBeatsSessionStamp(t *testing.T) {
	d := validDoc("beat-me", "result = 1")
	sha := ScriptSHA256(d.Script)
	now := time.Now()
	stamps := []Stamp{
		{ID: "beat-me", Rev: 1, ScriptSHA256: sha, RhinoVersion: "8", Status: StampFailed, By: BySession, At: now, Diagnostic: "x"},
		{ID: "beat-me", Rev: 1, ScriptSHA256: sha, RhinoVersion: "8", Status: StampPassed, By: ByHarness, At: now.Add(-time.Hour)},
	}
	v := VerifiedOn(d, stamps)
	if len(v.Passed) != 1 {
		t.Fatalf("the older harness pass should win over the newer session fail, got passed=%v failed=%v", v.Passed, v.Failed)
	}
}

func TestSidecarPruneDropsStaleStamps(t *testing.T) {
	d := validDoc("prune-me", "result = 1")
	corpus, err := LoadCorpus(bytes.NewReader([]byte(lines(t, d))), SourceSeed)
	if err != nil {
		t.Fatal(err)
	}
	sc := &Sidecar{Stamps: []Stamp{
		{ID: "prune-me", Rev: 1, ScriptSHA256: ScriptSHA256("result = 1"), RhinoVersion: "8", Status: StampPassed, By: ByHarness},
		{ID: "gone", Rev: 1, ScriptSHA256: ScriptSHA256("x"), RhinoVersion: "8", Status: StampPassed, By: ByHarness},
	}}
	kept, dropped := sc.Prune(corpus)
	if len(kept) != 1 || dropped != 1 {
		t.Errorf("want 1 kept 1 dropped, got kept=%d dropped=%d", len(kept), dropped)
	}
}

func TestWriteSidecarRoundTrips(t *testing.T) {
	stamps := []Stamp{
		{ID: "bravo", Rev: 1, ScriptSHA256: ScriptSHA256("x"), RhinoVersion: "8", Status: StampPassed, By: ByHarness, At: time.Now()},
		{ID: "alpha", Rev: 1, ScriptSHA256: ScriptSHA256("y"), RhinoVersion: "8", Status: StampPassed, By: ByHarness, At: time.Now()},
	}
	var buf bytes.Buffer
	if err := WriteSidecar(&buf, stamps); err != nil {
		t.Fatal(err)
	}
	sc, err := LoadSidecar(&buf)
	if err != nil {
		t.Fatal(err)
	}
	if len(sc.Stamps) != 2 {
		t.Fatalf("want 2 stamps round-tripped, got %d", len(sc.Stamps))
	}
	if sc.Stamps[0].ID != "alpha" {
		t.Errorf("stamps should be written sorted by id, got first=%q", sc.Stamps[0].ID)
	}
}
