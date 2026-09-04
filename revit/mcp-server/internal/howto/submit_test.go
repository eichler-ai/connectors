package howto

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
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
	// A second, DIFFERENT new submission with the same title gets a suffixed
	// id (an identical resend replaces the first; see
	// TestSaveResendOfTheSameSubmissionReplacesItsLocalFile).
	other := goodSubmission()
	other.Script = other.Script + "\n// a different document\n"
	saved2, err := Save(env, other)
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

// The broker labels a session stamp with its full version line -- main.go's
// versionLine() is "<version> (revision <sha> committed <time>...)", 53
// chars for a dev build and longer for a release -- while the stamp schema
// bounds connector_version at 40. Save built the stamp, watched
// ValidateStamp reject it, and dropped it silently, so every real
// submit_howto reported howto-script-not-run-this-session for a script that
// had just succeeded in this very session. A label the caller passes can
// never be allowed to cost the submitter the stamp.
func TestSaveStampsWhenConnectorVersionIsTheFullVersionLine(t *testing.T) {
	env := testEnv(t)
	env.ConnectorVersion = "v0.1.2 (revision 4079fc7 committed 2026-09-03T01:02:03Z)"
	if len(env.ConnectorVersion) <= MaxStampConnectorVersionLen {
		t.Fatalf("this test needs a label longer than the schema bound %d, got %d", MaxStampConnectorVersionLen, len(env.ConnectorVersion))
	}
	sub := goodSubmission()
	ranSHA := ScriptSHA256(sub.Script)
	env.SessionSucceeded = func(sha string) (time.Time, bool) { return env.Now(), sha == ranSHA }
	saved, err := Save(env, sub)
	if err != nil {
		t.Fatal(err)
	}
	if saved.Stamp == nil || saved.Stamp.By != BySession || saved.Stamp.Status != StampPassed {
		t.Fatalf("the exact script succeeded this session, so the save must carry a session stamp; got %+v", saved.Stamp)
	}
	if !strings.HasPrefix(env.ConnectorVersion, saved.Stamp.ConnectorVersion) || len(saved.Stamp.ConnectorVersion) > MaxStampConnectorVersionLen {
		t.Fatalf("connector_version must be the caller's label cut to the schema bound, got %q", saved.Stamp.ConnectorVersion)
	}
	side, err := os.ReadFile(filepath.Join(env.LocalDir, SessionSidecarName))
	if err != nil || !strings.Contains(string(side), `"by":"session"`) {
		t.Fatalf("the stamp must reach the sidecar too: %v %s", err, side)
	}
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
		Task:     "For nicholas on NICKS-MAC: export from C:\\Projects\\Tower B\\model.rvt to \\\\fileserver\\share\\out; mail nick@example.com; host 10.211.55.2.",
		Script:   "// exported from /Users/nicholas/dev/eichler/connectors/model.rvt\nvar p = \"file:///C:/Projects/x\";\nvar q = \"%USERPROFILE%\\RevitMCPExchange\";\n",
		Pitfalls: []Pitfall{{Symptom: "Project1 fails on D:\\data", Cause: "c", Fix: "f"}},
		Queries:  &Queries{Miss: []Query{{Text: "export Tower B Level 3 Coordination", Surfaced: "nothing"}}},
	}
	out, residue := Scrub(d, env)
	if len(residue) != 0 {
		t.Fatalf("unexpected residue: %+v", residue)
	}
	for field, text := range map[string]string{"title": out.Title, "task": out.Task, "script": out.Script, "pitfall": out.Pitfalls[0].Symptom, "query": out.Queries.Miss[0].Text} {
		for _, leak := range []string{"Tower B", "nicholas", "NICKS-MAC", "C:\\", "\\\\fileserver", "@example.com", "10.211", "/Users/", "file://", "%USERPROFILE%", "D:\\", "Project1", "model.rvt", "share\\out"} {
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
	if filepath.Dir(prep.OutboxDoc) != filepath.Join(filepath.Dir(env.LocalDir), "outbox") {
		t.Fatalf("outbox is not the sibling of local: %s", prep.OutboxDoc)
	}
	if strings.Contains(prep.GhCommand, "--template") || !strings.Contains(prep.GhCommand, "--label howto-submission") || !strings.Contains(prep.GhCommand, prep.BodyPath) {
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
	if len(prep2.Labels) != 2 || prep2.Labels[1] != "howto-edit" || !strings.Contains(prep2.GhCommand, "(rev 2)") || !strings.Contains(prep2.GhCommand, "--label howto-submission,howto-edit") {
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
		"ab":                         "howto-" + ScriptSHA256("ab")[:8],
		"墙体创建":                       "howto-" + ScriptSHA256("墙体创建")[:8],
		strings.Repeat("long-", 30):  strings.TrimRight(strings.Repeat("long-", 30)[:80], "-"),
	}
	for in, want := range cases {
		if got := Slug(in); got != want {
			t.Errorf("Slug(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestScrubKeepsCSharpLiteralsWholeAndRespectsWordBoundaries(t *testing.T) {
	env := testEnv(t)
	env.Username = "max"                  // too short to scrub as a word: Math.Max must survive
	env.DocumentTitles = []string{"Wall"} // too short as well
	d := &Document{
		Title:  "Wall.Create basics",
		Task:   "Use Math.Max and Wall.Create.",
		Script: "var p = \"C:\\\\Projects\\\\Tower B\\\\model.rvt\"; // opened from C:\\Projects\\Tower B\\model.rvt\nvar u = \"\\\\\\\\fileserver\\\\share\\\\x.rvt\";\n",
	}
	out, residue := Scrub(d, env)
	if len(residue) != 0 {
		t.Fatalf("residue: %+v", residue)
	}
	if out.Task != "Use Math.Max and Wall.Create." || out.Title != "Wall.Create basics" {
		t.Fatalf("short names must not be scrubbed: %q / %q", out.Task, out.Title)
	}
	if strings.Contains(out.Script, "Projects") || strings.Contains(out.Script, "model.rvt") || strings.Contains(out.Script, "fileserver") {
		t.Fatalf("path tails leaked: %s", out.Script)
	}
	// The literal's quotes survive so the scrubbed script is still a string.
	if !strings.Contains(out.Script, "var p = \"<path>\";") || !strings.Contains(out.Script, "var u = \"<path>\";") {
		t.Fatalf("literal structure broken: %s", out.Script)
	}
	env.Username = "nicholas"
	d2 := &Document{Task: "nicholas and Nicholas but not nicholasson or anicholas."}
	out2, _ := Scrub(d2, env)
	if out2.Task != "<user> and <user> but not nicholasson or anicholas." {
		t.Fatalf("boundary: %q", out2.Task)
	}
}

func TestPrepareRefusesResidueTheScrubberCannotRemove(t *testing.T) {
	env := testEnv(t)
	sub := goodSubmission()
	sub.Task = "Open the project file Tower-Model.rvt and tag every door on a level, then export it."
	saved, err := Save(env, sub)
	if err != nil {
		t.Fatal(err)
	}
	_, err = Prepare(env, saved, "")
	var un *ErrUnscrubbed
	if err == nil {
		t.Fatal("a bare project file name must be refused as residue")
	}
	if !errorsAsUnscrubbed(err, &un) || un.Residue[0].Field != "task" || un.Residue[0].Kind != "project-file" {
		t.Fatalf("err = %v", err)
	}
	if _, statErr := os.Stat(filepath.Join(filepath.Dir(env.LocalDir), "outbox", saved.Doc.ID+".json")); statErr == nil {
		t.Fatal("outbox must not be written on residue")
	}
}

func errorsAsUnscrubbed(err error, target **ErrUnscrubbed) bool {
	for err != nil {
		if u, ok := err.(*ErrUnscrubbed); ok {
			*target = u
			return true
		}
		w, ok := err.(interface{ Unwrap() error })
		if !ok {
			return false
		}
		err = w.Unwrap()
	}
	return false
}

func TestSessionStampIsNotDuplicatedOnResave(t *testing.T) {
	env := testEnv(t)
	sub := goodSubmission()
	ranSHA := ScriptSHA256(sub.Script)
	env.SessionSucceeded = func(sha string) (time.Time, bool) { return env.Now(), sha == ranSHA }
	for i := 0; i < 3; i++ {
		if _, err := Save(env, sub); err != nil {
			t.Fatal(err)
		}
		local, _ := LoadLocalDir(env.LocalDir)
		env.Bases = []*Corpus{local}
		sub.ID = "tag-every-door-on-a-level"
		sub.ChangeNote = "resave"
	}
	f, _ := os.Open(filepath.Join(env.LocalDir, SessionSidecarName))
	side, err := LoadSidecar(f)
	f.Close()
	if err != nil {
		t.Fatal(err)
	}
	// rev 1, rev 2, rev 3 each get one stamp (same script, different rev); no duplicates within a rev.
	if len(side.Stamps) != 3 {
		t.Fatalf("stamps = %d: %+v", len(side.Stamps), side.Stamps)
	}
}

func TestFileIssuePostsToTheIssuesAPIWithTheUsersToken(t *testing.T) {
	env := testEnv(t)
	saved, err := Save(env, goodSubmission())
	if err != nil {
		t.Fatal(err)
	}
	prep, err := Prepare(env, saved, "")
	if err != nil {
		t.Fatal(err)
	}
	var got struct {
		Title  string   `json:"title"`
		Body   string   `json:"body"`
		Labels []string `json:"labels"`
	}
	var auth, path string
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		auth, path = r.Header.Get("Authorization"), r.URL.Path
		json.NewDecoder(r.Body).Decode(&got)
		w.WriteHeader(http.StatusCreated)
		fmt.Fprint(w, `{"html_url":"https://github.com/eichler-ai/connectors/issues/171","number":171,"labels":[{"name":"howto-submission"}]}`)
	}))
	defer srv.Close()
	filed, err := FileIssue(context.Background(), srv.Client(), srv.URL, "eichler-ai/connectors", "ghp_test", prep)
	if err != nil {
		t.Fatal(err)
	}
	if filed.URL == "" || filed.Number != 171 || len(filed.LabelsApplied) != 1 {
		t.Fatalf("filed = %+v", filed)
	}
	if auth != "Bearer ghp_test" || path != "/repos/eichler-ai/connectors/issues" {
		t.Fatalf("request: auth=%q path=%q", auth, path)
	}
	if got.Title != prep.Title || got.Body != prep.Body || !strings.Contains(got.Body, "```json") || len(got.Labels) != 1 {
		t.Fatalf("payload = %+v", got)
	}
	// Failure is reported with the status and GitHub's message, and no token means no call.
	bad := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusNotFound)
		fmt.Fprint(w, `{"message":"Not Found"}`)
	}))
	defer bad.Close()
	if _, err := FileIssue(context.Background(), bad.Client(), bad.URL, "eichler-ai/connectors", "ghp_test", prep); err == nil || !strings.Contains(err.Error(), "404") {
		t.Fatalf("404 not surfaced: %v", err)
	}
	if _, err := FileIssue(context.Background(), nil, srv.URL, "eichler-ai/connectors", "", prep); err == nil {
		t.Fatal("no token must not attempt a request")
	}
}

func TestSaveResendOfTheSameSubmissionReplacesItsLocalFile(t *testing.T) {
	env := Env{LocalDir: filepath.Join(t.TempDir(), "local"), Now: func() time.Time { return time.Date(2026, 9, 2, 0, 0, 0, 0, time.UTC) }}
	sub := Submission{Title: "Tag every door on a level", Task: "Place a door tag on every door instance hosted on a given level using IndependentTag.Create in the level's plan view.",
		Script: "return 1;", Members: []string{"Autodesk.Revit.DB.IndependentTag.Create"}}
	first, err := Save(env, sub)
	if err != nil {
		t.Fatal(err)
	}
	// The gate's remedy: the same fields again, this time to confirm. The
	// tool loads the local corpus into Bases on every call, so the earlier
	// save is also visible there.
	local, _ := LoadLocalDir(env.LocalDir)
	env.Bases = []*Corpus{local}
	again, err := Save(env, sub)
	if err != nil {
		t.Fatal(err)
	}
	if again.Doc.ID != first.Doc.ID || !again.Replaced {
		t.Fatalf("a resend must replace the earlier local save, not mint a new id: first=%s again=%s replaced=%v", first.Doc.ID, again.Doc.ID, again.Replaced)
	}
	// A different document with the same title is still a new lineage.
	other := sub
	other.Script = "return 2;"
	third, err := Save(env, other)
	if err != nil {
		t.Fatal(err)
	}
	if third.Doc.ID == first.Doc.ID {
		t.Fatalf("a different script under the same title must not overwrite the first document")
	}
}

func TestSaveReportsAStampErrorNamingTheSavedPathWhenTheBrokerBuiltABadStamp(t *testing.T) {
	// Review of #208: the document is written before the stamp is validated, so a broker-side
	// stamp defect must surface as a typed error carrying the saved path -- not as the generic
	// save-failed remedy that sends the submitter to check a directory that is fine.
	env := testEnv(t)
	env.RevitVersion = "abcd" // violates the schema's ^20[2-9][0-9]$ -- every stamp field is the broker's own
	sub := goodSubmission()
	ranSHA := ScriptSHA256(sub.Script)
	env.SessionSucceeded = func(sha string) (time.Time, bool) { return env.Now(), sha == ranSHA }
	saved, err := Save(env, sub)
	var se *StampError
	if saved != nil || !errors.As(err, &se) {
		t.Fatalf("expected a *StampError, got saved=%v err=%v", saved, err)
	}
	if se.Path == "" {
		t.Fatal("StampError must name the path the document was saved at")
	}
	if _, statErr := os.Stat(se.Path); statErr != nil {
		t.Fatalf("the document must be on disk at %s: %v", se.Path, statErr)
	}
}
