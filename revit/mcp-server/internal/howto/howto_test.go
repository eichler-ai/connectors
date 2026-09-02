package howto

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func readExample(t *testing.T, name string) []byte {
	t.Helper()
	b, err := os.ReadFile(filepath.Join("testdata", name))
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func exampleNames(t *testing.T) []string {
	t.Helper()
	m, err := filepath.Glob(filepath.Join("testdata", "howto-example-*.json"))
	if err != nil || len(m) == 0 {
		t.Fatalf("no example fixtures: %v", err)
	}
	for i := range m {
		m[i] = filepath.Base(m[i])
	}
	return m
}

// The three published examples validate, and the copies under testdata are
// byte-identical to the ones in revit/docs (the human-facing references),
// so neither can drift from the other unnoticed.
func TestPublishedExamplesValidateAndMatchDocs(t *testing.T) {
	for _, name := range exampleNames(t) {
		raw := readExample(t, name)
		d, err := ValidateDocument(raw)
		if err != nil {
			t.Errorf("%s: %v", name, err)
			continue
		}
		if d.ID == "" || d.Rev != 1 {
			t.Errorf("%s: id=%q rev=%d", name, d.ID, d.Rev)
		}
		docsCopy := filepath.Join("..", "..", "..", "docs", name)
		if b, err := os.ReadFile(docsCopy); err == nil && !bytes.Equal(b, raw) {
			t.Errorf("%s differs between internal/howto/testdata and revit/docs; copy one over the other", name)
		}
	}
}

func mutate(t *testing.T, raw []byte, f func(m map[string]any)) []byte {
	t.Helper()
	var m map[string]any
	if err := json.Unmarshal(raw, &m); err != nil {
		t.Fatal(err)
	}
	f(m)
	out, _ := json.Marshal(m)
	return out
}

// Each case is a rule the schema or the cross-field validator must enforce;
// the message fragment pins WHICH rule fired so a loosened schema fails here.
func TestValidateDocumentRejects(t *testing.T) {
	base := readExample(t, "howto-example-group-edit-propagates.json")
	cases := []struct {
		name string
		f    func(m map[string]any)
		want string
	}{
		{"missing script on a howto", func(m map[string]any) { delete(m, "script") }, "script"},
		{"empty script", func(m map[string]any) { m["script"] = "" }, "script"},
		{"bad id", func(m map[string]any) { m["id"] = "Bad_ID" }, "id"},
		{"rev zero", func(m map[string]any) { m["rev"] = 0 }, "rev"},
		{"unqualified member", func(m map[string]any) { m["members"] = []any{"Wall.Create"} }, "members"},
		{"bad kind", func(m map[string]any) { m["kind"] = "recipe" }, "kind"},
		{"bad created_at", func(m map[string]any) { m["created_at"] = "yesterday" }, "created_at"},
		{"email handle", func(m map[string]any) {
			m["contributors"] = []any{map[string]any{"handle": "a@b.c", "role": "author", "rev": 1}}
		}, "handle"},
		{"contributor rev above doc rev", func(m map[string]any) {
			m["contributors"] = []any{map[string]any{"handle": "nick", "role": "author", "rev": 7}}
		}, "exceeds"},
		{"absorbs own id", func(m map[string]any) { m["absorbs"] = []any{m["id"]} }, "own id"},
		{"updated before created", func(m map[string]any) {
			m["created_at"] = "2026-09-02T00:00:00Z"
			m["updated_at"] = "2026-09-01T00:00:00Z"
		}, "before created_at"},
		{"hit without rank", func(m map[string]any) {
			m["queries"] = map[string]any{"hit": []any{map[string]any{"text": "x"}}}
		}, "needs rank"},
		{"miss without surfaced", func(m map[string]any) {
			m["queries"] = map[string]any{"miss": []any{map[string]any{"text": "x"}}}
		}, "needs surfaced"},
		{"submission without ref", func(m map[string]any) { m["provenance"] = map[string]any{"kind": "submission"} }, "ref"},
		{"local with reviewer", func(m map[string]any) {
			m["provenance"] = map[string]any{"kind": "local", "reviewed_by": "x"}
		}, "reviewed_by"},
		{"bad ranker", func(m map[string]any) {
			m["queries"] = map[string]any{"miss": []any{map[string]any{"text": "x", "surfaced": "y", "ranker": "legacy"}}}
		}, "ranker"},
	}
	for _, c := range cases {
		_, err := ValidateDocument(mutate(t, base, c.f))
		if err == nil {
			t.Errorf("%s: accepted", c.name)
			continue
		}
		if !strings.Contains(err.Error(), c.want) {
			t.Errorf("%s: error does not name the rule (%q): %v", c.name, c.want, err)
		}
	}
}

func TestUnknownFieldsAreAllowedAndPreserved(t *testing.T) {
	base := readExample(t, "howto-example-join-walls.json")
	raw := mutate(t, base, func(m map[string]any) { m["future_field"] = map[string]any{"x": 1}; m["schema_version"] = 3 })
	d, err := ValidateDocument(raw)
	if err != nil {
		t.Fatal(err)
	}
	if d.Extra["future_field"] == nil {
		t.Fatal("unknown field not preserved")
	}
	out, err := MarshalDocument(d)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(out), `"future_field"`) {
		t.Fatalf("re-marshalled document dropped the unknown field: %s", out)
	}
	c, err := LoadCorpus(bytes.NewReader(append(out, '\n')), SourceShared)
	if err != nil {
		t.Fatal(err)
	}
	if c.NewerThanBroker != 3 {
		t.Fatalf("NewerThanBroker = %d", c.NewerThanBroker)
	}
}

// compactLine turns a pretty-printed example into one JSONL line.
func compactLine(t *testing.T, raw []byte) []byte {
	t.Helper()
	var buf bytes.Buffer
	if err := json.Compact(&buf, raw); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

func corpusFromExamples(t *testing.T) *Corpus {
	t.Helper()
	var buf bytes.Buffer
	for _, name := range exampleNames(t) {
		buf.Write(compactLine(t, readExample(t, name)))
		buf.WriteByte('\n')
	}
	c, err := LoadCorpus(&buf, SourceSeed)
	if err != nil {
		t.Fatal(err)
	}
	return c
}

func TestCorpusLoadOneLinePerLineage(t *testing.T) {
	c := corpusFromExamples(t)
	if c.Len() != 3 || c.Skipped != 0 || c.Truncated {
		t.Fatalf("len=%d skipped=%d truncated=%v problems=%v", c.Len(), c.Skipped, c.Truncated, c.Problems)
	}
	d, redirected, ok := c.Get("group-edit-propagates")
	if !ok || redirected != "" || d.Kind != KindHowTo {
		t.Fatalf("Get: %v %q %v", ok, redirected, d)
	}
	// A duplicate id is a broken file, not two revisions.
	var buf bytes.Buffer
	line := compactLine(t, readExample(t, "howto-example-join-walls.json"))
	buf.Write(line)
	buf.WriteByte('\n')
	buf.Write(line)
	buf.WriteByte('\n')
	if _, err := LoadCorpus(&buf, SourceShared); err == nil || !strings.Contains(err.Error(), "duplicate id") {
		t.Fatalf("duplicate id not rejected: %v", err)
	}
	// An invalid line is skipped and counted, never fatal.
	buf.Reset()
	buf.Write(line)
	buf.WriteString("\n{\"schema_version\":1,\"id\":\"broken\"}\n")
	c2, err := LoadCorpus(&buf, SourceShared)
	if err != nil || c2.Len() != 1 || c2.Skipped != 1 || len(c2.Problems) != 1 {
		t.Fatalf("invalid line handling: err=%v len=%d skipped=%d problems=%v", err, c2.Len(), c2.Skipped, c2.Problems)
	}
}

func TestPutIsAnEditAtRevPlusOne(t *testing.T) {
	c := corpusFromExamples(t)
	d, _, _ := c.Get("group-edit-propagates")
	edit := *d
	edit.Rev = 1
	if err := c.Put(&edit); err == nil {
		t.Fatal("same rev accepted as an edit")
	}
	edit.Rev = 2
	edit.Title = "Edited"
	if err := c.Put(&edit); err != nil {
		t.Fatal(err)
	}
	got, _, _ := c.Get("group-edit-propagates")
	if got.Rev != 2 || got.Title != "Edited" || c.Len() != 3 {
		t.Fatalf("edit did not replace the line: rev=%d title=%q len=%d", got.Rev, got.Title, c.Len())
	}
	fresh := *d
	fresh.ID = "brand-new"
	fresh.Rev = 4
	if err := c.Put(&fresh); err == nil {
		t.Fatal("new lineage must start at rev 1")
	}
	var out bytes.Buffer
	if err := WriteCorpus(&out, c); err != nil {
		t.Fatal(err)
	}
	if n := bytes.Count(out.Bytes(), []byte{'\n'}); n != 3 {
		t.Fatalf("written corpus has %d lines, want 3", n)
	}
	// Sorted by id for stable diffs.
	lines := strings.Split(strings.TrimSpace(out.String()), "\n")
	if !strings.Contains(lines[0], `"id":"group-edit-propagates"`) {
		t.Fatalf("first line is not the alphabetically first id: %.80s", lines[0])
	}
}

func TestAbsorbRedirectsTheOldId(t *testing.T) {
	c := corpusFromExamples(t)
	if err := c.Absorb("group-edit-propagates", "group-member-move-silently-does-nothing"); err != nil {
		t.Fatal(err)
	}
	if c.Len() != 2 {
		t.Fatalf("len after absorb = %d", c.Len())
	}
	d, redirected, ok := c.Get("group-member-move-silently-does-nothing")
	if !ok || redirected != "group-edit-propagates" || d.ID != "group-edit-propagates" {
		t.Fatalf("absorbed id did not redirect: ok=%v redirected=%q", ok, redirected)
	}
	if err := c.Absorb("group-edit-propagates", "group-edit-propagates"); err == nil {
		t.Fatal("self-absorb accepted")
	}
	// Round-trips through the file: the survivor carries absorbs, the old line is gone,
	// and a reload still redirects.
	var out bytes.Buffer
	if err := WriteCorpus(&out, c); err != nil {
		t.Fatal(err)
	}
	c2, err := LoadCorpus(&out, SourceShared)
	if err != nil {
		t.Fatal(err)
	}
	if _, redirected, ok := c2.Get("group-member-move-silently-does-nothing"); !ok || redirected == "" {
		t.Fatal("redirect lost across write/load")
	}
}

func TestStampsMatchOnlyTheirRevisionAndScript(t *testing.T) {
	c := corpusFromExamples(t)
	d, _, _ := c.Get("group-edit-propagates")
	now := time.Date(2026, 9, 2, 12, 0, 0, 0, time.UTC)
	good := Stamp{ID: d.ID, Rev: d.Rev, ScriptSHA256: ScriptSHA256(d.Script), RevitVersion: "2025", Status: StampPassed, At: now, By: ByHarness}
	session2027 := Stamp{ID: d.ID, Rev: d.Rev, ScriptSHA256: ScriptSHA256(d.Script), RevitVersion: "2027", Status: StampFailed, At: now, By: BySession, Diagnostic: "boom"}
	harness2027 := Stamp{ID: d.ID, Rev: d.Rev, ScriptSHA256: ScriptSHA256(d.Script), RevitVersion: "2027", Status: StampPassed, At: now.Add(-time.Hour), By: ByHarness}
	staleRev := Stamp{ID: d.ID, Rev: 99, ScriptSHA256: ScriptSHA256(d.Script), RevitVersion: "2025", Status: StampPassed, At: now, By: ByHarness}
	staleScript := Stamp{ID: d.ID, Rev: d.Rev, ScriptSHA256: ScriptSHA256(d.Script + " "), RevitVersion: "2025", Status: StampPassed, At: now, By: ByHarness}
	orphan := Stamp{ID: "gone", Rev: 1, ScriptSHA256: ScriptSHA256(""), RevitVersion: "2025", Status: StampPassed, At: now, By: ByHarness}

	v := VerifiedOn(d, []Stamp{good, session2027, harness2027, staleRev, staleScript, orphan})
	if strings.Join(v.Passed, ",") != "2025,2027" || len(v.Failed) != 0 {
		t.Fatalf("passed=%v failed=%v (an older harness pass must beat a newer session fail)", v.Passed, v.Failed)
	}
	side := &Sidecar{Stamps: []Stamp{good, session2027, harness2027, staleRev, staleScript, orphan}}
	kept, dropped := side.Prune(c)
	if len(kept) != 3 || dropped != 3 {
		t.Fatalf("prune kept %d dropped %d", len(kept), dropped)
	}
	// Sidecar round trip: a failed stamp needs its diagnostic; the schema
	// enforces it and the loader counts the offender.
	var out bytes.Buffer
	if err := WriteSidecar(&out, append(kept, Stamp{ID: d.ID, Rev: d.Rev, ScriptSHA256: ScriptSHA256(d.Script), RevitVersion: "2026", Status: StampFailed, At: now, By: ByHarness})); err != nil {
		t.Fatal(err)
	}
	s2, err := LoadSidecar(&out)
	if err != nil {
		t.Fatal(err)
	}
	if len(s2.Stamps) != 3 || s2.Skipped != 1 {
		t.Fatalf("sidecar reload: stamps=%d skipped=%d problems=%v", len(s2.Stamps), s2.Skipped, s2.Problems)
	}
}

func TestOverlayLocalOverSharedRules(t *testing.T) {
	shared := corpusFromExamples(t)
	sd, _, _ := shared.Get("group-edit-propagates")

	// Identical local copy (same script and task): shared is served, notice says delete.
	same := *sd
	same.Provenance = Provenance{Kind: ProvenanceLocal}
	local := &Corpus{Source: SourceLocal, docs: map[string]*Document{same.ID: &same}, order: []string{same.ID}, absorbed: map[string]string{}}
	o, ok := Overlay(shared, local, sd.ID)
	if !ok || o.Source != SourceSeed || o.Doc != sd || !strings.Contains(o.Notice, "identical") {
		t.Fatalf("identical local: %+v", o)
	}

	// Differing local copy: local wins, marked local, shared rev reported.
	diff := *sd
	diff.Script = sd.Script + "\n// my site's variant\n"
	diff.Rev = 1
	local.docs[sd.ID] = &diff
	o, ok = Overlay(shared, local, sd.ID)
	if !ok || o.Source != SourceLocal || o.Doc != &diff || o.SharedRev != 1 || o.Notice != "" {
		t.Fatalf("differing local at same rev: %+v", o)
	}
	// Shared moved on: notice names both revisions.
	moved := *sd
	moved.Rev = 3
	shared.docs[sd.ID] = &moved
	o, _ = Overlay(shared, local, sd.ID)
	if o.SharedRev != 3 || !strings.Contains(o.Notice, "rev 3") {
		t.Fatalf("shared moved on: %+v", o)
	}
	// Local-only and shared-only.
	if o, ok := Overlay(shared, local, "group-member-move-silently-does-nothing"); !ok || o.Source != SourceSeed {
		t.Fatalf("shared-only: %+v", o)
	}
	onlyLocal := *sd
	onlyLocal.ID = "my-private-recipe"
	local.docs[onlyLocal.ID] = &onlyLocal
	if o, ok := Overlay(shared, local, "my-private-recipe"); !ok || o.Source != SourceLocal || o.SharedRev != 0 {
		t.Fatalf("local-only: %+v", o)
	}
	if _, ok := Overlay(shared, local, "nope"); ok {
		t.Fatal("unknown id reported present")
	}
}
