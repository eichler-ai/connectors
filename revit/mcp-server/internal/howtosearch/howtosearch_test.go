package howtosearch

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/howto"
)

// The tests run lexical-only against the real embedded corpus (the seed,
// verified on 2025 and 2027) plus a temp local directory. Rank assertions
// are on the lexical pass alone, so they pin the field set and the
// overlay, not the dense/reranked quality the live harness measures.

func seedDoc(t *testing.T, id string) *howto.Document {
	t.Helper()
	c, _, _, err := howto.Embedded()
	if err != nil {
		t.Fatal(err)
	}
	d, _, ok := c.Get(id)
	if !ok {
		t.Fatalf("seed has no %s", id)
	}
	return d
}

func writeLocal(t *testing.T, dir string, d *howto.Document) {
	t.Helper()
	raw, err := howto.MarshalDocument(d)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, d.ID+".json"), raw, 0o644); err != nil {
		t.Fatal(err)
	}
}

func ids(hits []hit) []string {
	var out []string
	for _, h := range hits {
		out = append(out, h.Doc.Doc.ID)
	}
	return out
}

type hit = struct {
	Doc   Entry
	Score float64
}

func toHits(r Result) []hit {
	out := make([]hit, len(r.Hits))
	for i, h := range r.Hits {
		out[i] = hit{Doc: h.Doc, Score: h.Score}
	}
	return out
}

func TestSearchFindsASeedDocumentByTask(t *testing.T) {
	s := New(filepath.Join(t.TempDir(), "local"), nil, nil, t.Logf)
	res, err := s.Search(context.Background(), "enclose a rectangular footprint with walls and confirm the corners join", "2027")
	if err != nil {
		t.Fatal(err)
	}
	got := ids(toHits(res))
	if len(got) == 0 || got[0] != "walls-create-and-join" {
		t.Fatalf("rank 1 = %v", got)
	}
	if !res.Hits[0].Doc.VerifiedOn("2027") || !res.Hits[0].Doc.VerifiedOn("2025") {
		t.Errorf("seed documents are verified on both versions; got %+v", res.Hits[0].Doc.Verified)
	}
	if res.Hits[0].Doc.Source != howto.SourceSeed {
		t.Errorf("source = %q", res.Hits[0].Doc.Source)
	}
	if res.Status.Documents == 0 || res.Status.Version.Hash == "" {
		t.Errorf("status should describe the corpus: %+v", res.Status)
	}
	if res.Dense || res.Reranked {
		t.Errorf("no models wired: dense=%v reranked=%v", res.Dense, res.Reranked)
	}
}

func TestLocalDocumentsAreIndexedAndRescannedOnChange(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "local")
	s := New(dir, nil, nil, t.Logf)
	ctx := context.Background()
	before, err := s.Search(ctx, "purple zebra ramp", "2027")
	if err != nil {
		t.Fatal(err)
	}
	if len(before.Hits) != 0 {
		t.Fatalf("nothing should match yet: %v", ids(toHits(before)))
	}

	d := seedDoc(t, "floors-create-from-loop")
	local := *d
	local.ID, local.Rev = "purple-zebra-ramp", 1
	local.Title = "Build a purple zebra ramp"
	local.Task = "Create a purple zebra ramp between two levels; the task names the ramp type and the levels."
	local.Absorbs = nil
	local.Provenance = howto.Provenance{Kind: howto.ProvenanceLocal}
	writeLocal(t, dir, &local)

	after, err := s.Search(ctx, "purple zebra ramp", "2027")
	if err != nil {
		t.Fatal(err)
	}
	if got := ids(toHits(after)); len(got) != 1 || got[0] != "purple-zebra-ramp" {
		t.Fatalf("local document should be indexed after the directory changed: %v", got)
	}
	if after.Hits[0].Doc.Source != howto.SourceLocal {
		t.Errorf("source = %q", after.Hits[0].Doc.Source)
	}
	if after.Hits[0].Doc.VerifiedOn("2027") {
		t.Errorf("a local document with no stamp is not verified")
	}
	if after.Fingerprint == before.Fingerprint {
		t.Errorf("the fingerprint must change with the local directory so cursors do not survive it")
	}
	if after.Status.Local != 1 {
		t.Errorf("status.Local = %d", after.Status.Local)
	}
}

func TestVersionPreferenceLeadsWithVerifiedHereWithoutFiltering(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "local")
	// A local copy of the walls task under another id, unverified: on its
	// own it out-scores the seed lexically (title and task both repeat the
	// query words), so the version preference is what puts the verified
	// seed first -- and only for a version the seed is verified on.
	d := seedDoc(t, "walls-create-and-join")
	local := *d
	local.ID, local.Rev, local.Absorbs = "walls-enclose-footprint-local", 1, nil
	local.Title = "Enclose a rectangular footprint with walls and confirm the corners join"
	local.Provenance = howto.Provenance{Kind: howto.ProvenanceLocal}
	writeLocal(t, dir, &local)
	s := New(dir, nil, nil, t.Logf)
	ctx := context.Background()
	q := "enclose a rectangular footprint with walls and confirm the corners join"

	unknown, _ := s.Search(ctx, q, "2099")
	got := ids(toHits(unknown))
	if len(got) < 2 || got[0] != "walls-enclose-footprint-local" {
		t.Fatalf("with no document verified on 2099 the ranked order stands; got %v", got)
	}
	known, _ := s.Search(ctx, q, "2027")
	got = ids(toHits(known))
	if got[0] != "walls-create-and-join" {
		t.Errorf("on 2027 the verified seed should lead; got %v", got)
	}
	found := false
	for _, id := range got {
		found = found || id == "walls-enclose-footprint-local"
	}
	if !found {
		t.Errorf("the unverified local document must still be returned: %v", got)
	}
}

func TestLocalOverlayOfASeedLineage(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "local")
	s := New(dir, nil, nil, t.Logf)
	ctx := context.Background()
	d := seedDoc(t, "text-notes-and-annotation-text")

	// Identical script: the seed is served and the local copy is reported
	// superseded.
	same := *d
	same.Provenance = howto.Provenance{Kind: howto.ProvenanceLocal}
	writeLocal(t, dir, &same)
	e, _, _, ok, err := s.Describe(ctx, d.ID)
	if err != nil || !ok {
		t.Fatalf("describe: ok=%v err=%v", ok, err)
	}
	if e.Source != howto.SourceSeed || e.Override.Code != howto.CodeLocalSupersededByShared {
		t.Errorf("identical local copy: source=%q code=%q", e.Source, e.Override.Code)
	}

	// Edited script at the next revision: the local one is served, marked
	// local, with the seed's revision alongside.
	edited := *d
	edited.Rev = d.Rev + 1
	edited.Script = d.Script + "\n// local edit\n"
	edited.Provenance = howto.Provenance{Kind: howto.ProvenanceLocal}
	writeLocal(t, dir, &edited)
	e, _, _, ok, err = s.Describe(ctx, d.ID)
	if err != nil || !ok {
		t.Fatalf("describe: ok=%v err=%v", ok, err)
	}
	if e.Source != howto.SourceLocal || e.Override.SharedRev != d.Rev || e.Doc.Rev != d.Rev+1 {
		t.Errorf("edited local copy: source=%q sharedRev=%d rev=%d", e.Source, e.Override.SharedRev, e.Doc.Rev)
	}
	if e.VerifiedOn("2027") {
		t.Errorf("the seed's stamps must not carry over to a changed script")
	}
}

func TestDescribeFollowsAnAbsorbsPointer(t *testing.T) {
	s := New(filepath.Join(t.TempDir(), "local"), nil, nil, t.Logf)
	e, from, _, ok, err := s.Describe(context.Background(), "walls-closed-footprint-confirm-joins")
	if err != nil || !ok {
		t.Fatalf("ok=%v err=%v", ok, err)
	}
	if e.Doc.ID != "walls-create-and-join" || from != "walls-closed-footprint-confirm-joins" {
		t.Errorf("got %s (from %q)", e.Doc.ID, from)
	}
	if _, _, _, ok, _ := s.Describe(context.Background(), "no-such-howto"); ok {
		t.Errorf("unknown id must not resolve")
	}
}

func TestSessionStampsInTheLocalSidecarCount(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "local")
	d := seedDoc(t, "floors-create-from-loop")
	local := *d
	local.ID, local.Rev, local.Absorbs = "floors-local-copy", 1, nil
	local.Provenance = howto.Provenance{Kind: howto.ProvenanceLocal}
	writeLocal(t, dir, &local)
	st := howto.Stamp{ID: local.ID, Rev: 1, ScriptSHA256: howto.ScriptSHA256(local.Script), RevitVersion: "2025",
		Status: howto.StampPassed, At: time.Now().UTC(), By: howto.BySession}
	raw, _ := json.Marshal(st)
	if err := os.WriteFile(filepath.Join(dir, howto.SessionSidecarName), append(raw, '\n'), 0o644); err != nil {
		t.Fatal(err)
	}
	s := New(dir, nil, nil, t.Logf)
	e, _, _, ok, err := s.Describe(context.Background(), "floors-local-copy")
	if err != nil || !ok {
		t.Fatalf("ok=%v err=%v", ok, err)
	}
	if !e.VerifiedOn("2025") || e.VerifiedOn("2027") {
		t.Errorf("session stamp should verify 2025 only: %+v", e.Verified)
	}
	if e.Verified.ByVersion["2025"].By != howto.BySession {
		t.Errorf("by = %q", e.Verified.ByVersion["2025"].By)
	}
}
