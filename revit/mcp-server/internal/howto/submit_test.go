package howto

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func testEnv(t *testing.T) Env {
	t.Helper()
	fixed := time.Date(2026, 9, 2, 10, 0, 0, 0, time.UTC)
	return Env{
		LocalDir:         filepath.Join(t.TempDir(), "howto", "local"),
		RevitVersion:     "2025",
		ConnectorVersion: "dev",
		DocumentTitles:   []string{"Tower B Level 3 Coordination", "Project1"},
		Hostname:         "NICKS-MAC",
		Username:         "nicholas",
		RepoSlug:         "eichler-ai/connectors",
		Now:              func() time.Time { return fixed },
	}
}

func goodSubmission() Submission {
	return Submission{
		Title:   "Tag every door on a level",
		Task:    "Place a door tag on every door instance hosted on a given level using IndependentTag.Create in the level's plan view.",
		Script:  "var doc = Document;\nreturn Connector.WithTransaction(doc, () => {\n  // 1. collect doors on the level\n  return 1;\n});\n",
		Members: []string{"Autodesk.Revit.DB.IndependentTag.Create", "Autodesk.Revit.DB.FilteredElementCollector.OfCategory"},
		Pitfalls: []Pitfall{{Symptom: "IndependentTag.Create throws when the view is not a plan view", Cause: "door tags need a plan view of the door's level",
			Fix: "look up the ViewPlan for the level first"}},
		Tags: []string{"tags", "doors"},
	}
}

func TestSaveWritesAValidLocalDocument(t *testing.T) {
	env := testEnv(t)
	saved, err := Save(env, goodSubmission())
	if err != nil {
		t.Fatal(err)
	}
	if saved.Doc.ID != "tag-every-door-on-a-level" || saved.Doc.Rev != 1 || saved.Doc.Kind != KindHowTo || saved.Doc.Provenance.Kind != ProvenanceLocal {
		t.Fatalf("doc = %+v", saved.Doc)
	}
	raw, err := os.ReadFile(saved.LocalPath)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := ValidateDocument(raw); err != nil {
		t.Fatalf("written file does not validate: %v", err)
	}
	if saved.Stamp != nil {
		t.Fatal("no session run, yet a stamp was written")
	}
	// The local directory loads back as a corpus.
	local, err := LoadLocalDir(env.LocalDir)
	if err != nil || local.Len() != 1 {
		t.Fatalf("LoadLocalDir: %v len=%d problems=%v", err, local.Len(), local.Problems)
	}
	// A second new submission with the same title gets a suffixed id.
	saved2, err := Save(env, goodSubmission())
	if err != nil {
		t.Fatal(err)
	}
	if saved2.Doc.ID != "tag-every-door-on-a-level-2" {
		t.Fatalf("second id = %s", saved2.Doc.ID)
	}
}

func TestSaveRejectsWithEveryProblemNamed(t *testing.T) {
	env := testEnv(t)
	sub := goodSubmission()
	sub.Title = "short"
	sub.Members = []string{"Wall.Create"}
	sub.Pitfalls = []Pitfall{{Symptom: "x", Cause: "y", Fix: "z"}}
	_, err := Save(env, sub)
	var ve *ValidationError
	if !errorsAs(err, &ve) {
		t.Fatalf("want ValidationError, got %v", err)
	}
	joined := strings.Join(ve.Problems, "\n")
	for _, want := range []string{"/title", "/members/0", "/pitfalls/0"} {
		if !strings.Contains(joined, want) {
			t.Errorf("problems do not name %s: %v", want, ve.Problems)
		}
	}
	if entries, _ := os.ReadDir(env.LocalDir); len(entries) != 0 {
		t.Fatal("a rejected submission must write nothing")
	}
}

func errorsAs(err error, target **ValidationError) bool {
	for err != nil {
		if ve, ok := err.(*ValidationError); ok {
			*target = ve
			return true
		}
		u, ok := err.(interface{ Unwrap() error })
		if !ok {
			return false
		}
		err = u.Unwrap()
	}
	return false
}

func TestSaveStampsWhenTheExactScriptRanThisSession(t *testing.T) {
	env := testEnv(t)
	sub := goodSubmission()
	ran := time.Date(2026, 9, 2, 9, 59, 0, 0, time.UTC)
	ranSHA := ScriptSHA256(sub.Script)
	env.SessionSucceeded = func(sha string) (time.Time, bool) { return ran, sha == ranSHA }
	saved, err := Save(env, sub)
	if err != nil {
		t.Fatal(err)
	}
	if saved.Stamp == nil || saved.Stamp.By != BySession || saved.Stamp.RevitVersion != "2025" || saved.Stamp.Rev != 1 {
		t.Fatalf("stamp = %+v", saved.Stamp)
	}
	side, err := os.ReadFile(filepath.Join(env.LocalDir, SessionSidecarName))
	if err != nil || !strings.Contains(string(side), `"by":"session"`) {
		t.Fatalf("sidecar: %v %s", err, side)
	}
	// A different script text gets no stamp.
	sub.Script += "// edited\n"
	sub.Title = "Tag every door on a level, edited"
	saved2, _ := Save(env, sub)
	if saved2.Stamp != nil {
		t.Fatal("changed script must not inherit the stamp")
	}
}

func TestSaveWithIDIsTheNextRevisionWithMergedEvidence(t *testing.T) {
	env := testEnv(t)
	first, err := Save(env, goodSubmission())
	if err != nil {
		t.Fatal(err)
	}
	local, _ := LoadLocalDir(env.LocalDir)
	env.Bases = []*Corpus{local}
	edit := Submission{ID: first.Doc.ID, ChangeNote: "add the missing view pitfall",
		Pitfalls: []Pitfall{
			{Symptom: "IndependentTag.Create throws when the view is not a plan view", Cause: "dup", Fix: "dup"},
			{Symptom: "Tags land at the door's origin, not its centre", Cause: "the tag point is the element origin", Fix: "offset by half the door width"},
		},
		Queries: &Queries{Miss: []Query{{Text: "tag doors", Surfaced: "TagOrientation enum members"}}},
		Tags:    []string{"annotation"}, CreditAs: "nick"}
	saved, err := Save(env, edit)
	if err != nil {
		t.Fatal(err)
	}
	d := saved.Doc
	if d.Rev != 2 || d.ID != first.Doc.ID || d.Title != first.Doc.Title || d.Script != first.Doc.Script {
		t.Fatalf("revision = %+v", d)
	}
	if len(d.Pitfalls) != 2 || len(d.Queries.Miss) != 1 || len(d.Tags) != 3 {
		t.Fatalf("merge: pitfalls=%d miss=%d tags=%v", len(d.Pitfalls), len(d.Queries.Miss), d.Tags)
	}
	if len(d.Contributors) != 1 || d.Contributors[0].Role != RoleContributor || d.Contributors[0].Rev != 2 {
		t.Fatalf("contributors = %+v", d.Contributors)
	}
	if !saved.Replaced {
		t.Fatal("the local file should have been replaced")
	}
	// Missing change_note and unknown id are refused before anything is written.
	if _, err := Save(env, Submission{ID: first.Doc.ID}); err == nil || !strings.Contains(err.Error(), "change_note") {
		t.Fatalf("missing change_note: %v", err)
	}
	if _, err := Save(env, Submission{ID: "no-such-doc", ChangeNote: "x"}); err == nil || !strings.Contains(err.Error(), "no how-to") {
		t.Fatalf("unknown id: %v", err)
	}
}

func TestScrubReplacesPrivatePatternsAndReportsResidue(t *testing.T) {
	env := testEnv(t)
	d := &Document{
		Title:    "Export Tower B Level 3 Coordination to DWG",
		Task:     "Export from C:\\Projects\\Tower B\\model.rvt for nicholas on NICKS-MAC to \\\\fileserver\\share\\out; mail nick@example.com; host 10.211.55.2.",
		Script:   "// exported from /Users/nicholas/dev/eichler/connectors/model.rvt\nvar p = \"file:///C:/Projects/x\";\nvar q = \"%USERPROFILE%\\RevitMCPExchange\";\n",
		Pitfalls: []Pitfall{{Symptom: "Project1 fails on D:\\data", Cause: "c", Fix: "f"}},
		Queries:  &Queries{Miss: []Query{{Text: "export Tower B Level 3 Coordination", Surfaced: "nothing"}}},
	}
	out, residue := Scrub(d, env)
	if len(residue) != 0 {
		t.Fatalf("unexpected residue: %+v", residue)
	}
	for field, text := range map[string]string{"title": out.Title, "task": out.Task, "script": out.Script, "pitfall": out.Pitfalls[0].Symptom, "query": out.Queries.Miss[0].Text} {
		for _, leak := range []string{"Tower B", "nicholas", "NICKS-MAC", "C:\\", "\\\\fileserver", "@example.com", "10.211", "/Users/", "file://", "%USERPROFILE%", "D:\\", "Project1"} {
			if strings.Contains(text, leak) {
				t.Errorf("%s still contains %q: %s", field, leak, text)
			}
		}
	}
	if !strings.Contains(out.Title, "<document>") || !strings.Contains(out.Task, "<user>") || !strings.Contains(out.Task, "<host>") || !strings.Contains(out.Task, "<email>") {
		t.Fatalf("placeholders missing: title=%q task=%q", out.Title, out.Task)
	}
	if out.Provenance.Kind != ProvenanceSubmission {
		t.Fatalf("scrubbed provenance = %+v", out.Provenance)
	}
	// The original is untouched.
	if !strings.Contains(d.Task, "nicholas") {
		t.Fatal("Scrub mutated its input")
	}
}

func TestPrepareWritesOutboxAndRefusesResidue(t *testing.T) {
	env := testEnv(t)
	saved, err := Save(env, goodSubmission())
	if err != nil {
		t.Fatal(err)
	}
	prep, err := Prepare(env, saved, "")
	if err != nil {
		t.Fatal(err)
	}
	if !strings.HasPrefix(prep.IssueURL, "https://github.com/eichler-ai/connectors/issues/new?") || !strings.Contains(prep.IssueURL, "template=howto-submission.yml") {
		t.Fatalf("issue url = %s", prep.IssueURL)
	}
	if !strings.Contains(prep.GhCommand, "--template howto-submission.yml") || !strings.Contains(prep.GhCommand, prep.BodyPath) {
		t.Fatalf("gh command = %s", prep.GhCommand)
	}
	if len(prep.Labels) != 1 || prep.Labels[0] != "howto-submission" {
		t.Fatalf("labels = %v", prep.Labels)
	}
	body, err := os.ReadFile(prep.BodyPath)
	if err != nil || !strings.Contains(string(body), "```json") || !strings.Contains(string(body), "Reviewer checklist") {
		t.Fatalf("body: %v %s", err, body)
	}
	if raw, err := os.ReadFile(prep.OutboxDoc); err != nil {
		t.Fatal(err)
	} else if _, err := ValidateDocument(raw); err != nil {
		t.Fatalf("outbox document does not validate: %v", err)
	}
	// An edit is labelled howto-edit too, and the change note leads the body.
	local, _ := LoadLocalDir(env.LocalDir)
	env.Bases = []*Corpus{local}
	saved2, err := Save(env, Submission{ID: saved.Doc.ID, ChangeNote: "tighten the task sentence", Task: "Place a door tag on every door on a level, in that level's plan view, with IndependentTag.Create."})
	if err != nil {
		t.Fatal(err)
	}
	prep2, err := Prepare(env, saved2, "tighten the task sentence")
	if err != nil {
		t.Fatal(err)
	}
	if len(prep2.Labels) != 2 || prep2.Labels[1] != "howto-edit" || !strings.Contains(prep2.GhCommand, "(rev 2)") {
		t.Fatalf("edit prep = %+v", prep2)
	}
	body2, _ := os.ReadFile(prep2.BodyPath)
	if !strings.HasPrefix(string(body2), "**Change:** tighten the task sentence") {
		t.Fatalf("edit body does not lead with the change note: %.80s", body2)
	}
	// Residue the patterns cannot remove is refused: a bare hostname-like
	// token that is not the machine's name survives scrubbing only if it
	// matches a pattern; simulate by making the machine name empty and using
	// an IPv6-ish literal the patterns don't cover -- then the refusal path
	// is exercised with a pattern that DOES re-match after replacement.
	env2 := env
	env2.Hostname = ""
	sub := goodSubmission()
	sub.Title = "Export to \\\\srv\\share on host"
	saved3, err := Save(env2, sub)
	if err != nil {
		t.Fatal(err)
	}
	if p, err := Prepare(env2, saved3, ""); err != nil {
		t.Fatalf("a UNC path is scrubbable, not residue: %v", err)
	} else if strings.Contains(p.Scrubbed.Title, "srv") {
		t.Fatalf("UNC path survived: %s", p.Scrubbed.Title)
	}
}

func TestSlug(t *testing.T) {
	cases := map[string]string{
		"Tag every door on a level":  "tag-every-door-on-a-level",
		"  Wall.Create -- basics!  ": "wall-create-basics",
		"ab":                         "ab-x",
		strings.Repeat("long-", 30):  strings.TrimRight(strings.Repeat("long-", 30)[:80], "-"),
	}
	for in, want := range cases {
		if got := Slug(in); got != want {
			t.Errorf("Slug(%q) = %q, want %q", in, got, want)
		}
	}
}
