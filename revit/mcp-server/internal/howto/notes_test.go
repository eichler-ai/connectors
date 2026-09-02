package howto

import (
	"strings"
	"testing"
	"time"
)

func notesDoc(id string, rev int, script string) *Document {
	return &Document{SchemaVersion: SchemaVersion, ID: id, Rev: rev, Kind: KindHowTo, Title: "Title of " + id,
		Task:    "A task sentence long enough to pass validation, naming an element type and an operation for " + id + ".",
		Members: []string{"Autodesk.Revit.DB.Wall.Create"}, Script: script, ScriptLang: "csharp-script",
		Provenance: Provenance{Kind: ProvenanceHarness}, CreatedAt: time.Now().UTC(), UpdatedAt: time.Now().UTC()}
}

func corpusOf(t *testing.T, docs ...*Document) *Corpus {
	t.Helper()
	// Built directly rather than through Put, which enforces the edit
	// protocol (new lineages start at rev 1) a loaded snapshot need not obey.
	c := &Corpus{Source: SourceSeed, docs: map[string]*Document{}, absorbed: map[string]string{}}
	for _, d := range docs {
		c.docs[d.ID] = d
		c.order = append(c.order, d.ID)
	}
	return c
}

func TestDiffCorpusClassifiesEveryKindOfChange(t *testing.T) {
	old := corpusOf(t,
		notesDoc("kept", 1, "return 1;"),
		notesDoc("revised", 1, "return 1;"),
		notesDoc("absorbed", 1, "return 1;"),
		notesDoc("gone", 1, "return 1;"),
		notesDoc("script-only", 1, "return 1;"),
	)
	cur := corpusOf(t,
		notesDoc("kept", 1, "return 1;"),
		notesDoc("revised", 2, "return 2;"),
		notesDoc("survivor", 1, "return 1;"),
		notesDoc("script-only", 1, "return 99;"),
		notesDoc("added", 1, "return 1;"),
	)
	// The pointer a loaded corpus carries for a merged-away lineage.
	cur.absorbed["absorbed"] = "survivor"
	ch := DiffCorpus(old, cur)
	if got := strings.Join(ch.Added, ","); got != "added,survivor" {
		t.Errorf("added = %q", got)
	}
	if r, ok := ch.Revised["revised"]; !ok || r != [2]int{1, 2} {
		t.Errorf("revised = %v", ch.Revised)
	}
	if ch.Merged["absorbed"] != "survivor" {
		t.Errorf("merged = %v", ch.Merged)
	}
	if got := strings.Join(ch.Removed, ","); got != "gone" {
		t.Errorf("removed = %q", got)
	}
	if got := strings.Join(ch.ScriptOnly, ","); got != "script-only" {
		t.Errorf("scriptOnly = %q", got)
	}
	if _, touched := ch.Revised["kept"]; touched {
		t.Errorf("an unchanged document must not be reported")
	}
}

func TestDiffCorpusFirstReleaseAddsEverything(t *testing.T) {
	cur := corpusOf(t, notesDoc("a", 1, "x"), notesDoc("b", 1, "y"))
	ch := DiffCorpus(nil, cur)
	if len(ch.Added) != 2 || len(ch.Revised) != 0 {
		t.Fatalf("%+v", ch)
	}
}

func TestReleaseNotesNameEveryChangeAndNeverGoSilent(t *testing.T) {
	cur := corpusOf(t, notesDoc("walls", 2, "x"), notesDoc("floors", 1, "y"))
	cur.absorbed["old-walls"] = "walls"
	ch := CorpusChanges{Added: []string{"floors"}, Revised: map[string][2]int{"walls": {1, 2}}, Merged: map[string]string{"old-walls": "walls"}, Removed: []string{"gone"}, ScriptOnly: []string{"floors"}}
	out := ReleaseNotes(ch, cur, Version{Documents: 2, Hash: "abc123", VerifiedOn: []string{"2025", "2027"}})
	for _, want := range []string{"## How-tos", "2 documents, hash abc123", "verified on Revit 2025, 2027",
		"**Added**", "`floors` — Title of floors", "**Revised**", "`walls` (rev 1 → 2)", "**Merged**", "`old-walls` → `walls`", "**Removed**", "`gone`", "without a revision bump"} {
		if !strings.Contains(out, want) {
			t.Errorf("notes missing %q:\n%s", want, out)
		}
	}
	quiet := ReleaseNotes(CorpusChanges{}, cur, Version{Documents: 2, Hash: "abc123"})
	if !strings.Contains(quiet, "No how-to changed") {
		t.Errorf("an empty change set must still say so:\n%s", quiet)
	}
}

func TestReleaseNotesForTheEmbeddedSeedAgainstItself(t *testing.T) {
	cur, _, ver, err := Embedded()
	if err != nil {
		t.Fatal(err)
	}
	ch := DiffCorpus(cur, cur)
	if !ch.IsEmpty() {
		t.Fatalf("a corpus diffed against itself changed: %+v", ch)
	}
	if !strings.Contains(ReleaseNotes(ch, cur, ver), ver.Hash) {
		t.Errorf("notes should carry the corpus hash")
	}
}
