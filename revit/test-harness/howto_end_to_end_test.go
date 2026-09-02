//go:build harness

// The how-to corpus series' batch verifier (revit/docs/howto-seed-plan.md
// §6 step 6): one live test that drives the whole loop the six PRs built,
// against whichever Revit is connected, so the pieces are proven to work
// TOGETHER rather than each behind its own unit tests.
//
//	agent session ──submit_howto──▶ local corpus + session stamp + outbox
//	      │ (triage, mechanical: accept as a new lineage)
//	      ▼
//	corpus/<id>.json + sweep stamp ──go build -overlay──▶ a broker embedding it
//	      │
//	      ▼
//	search_howtos rank 1 / describe_howto shows the sweep stamp for THIS version
//	      │ (a revision of the same lineage goes round again and replaces it)
//
// What is mechanical here is exactly what /triage-howto-submission does by
// hand minus the human judgement (fold vs. new lineage, prose edits): the
// scrubbed outbox document is accepted as-is under provenance
// "submission", swept on a blank fixture through the same sweepOne the
// corpus sweep uses, and embedded by rebuilding the broker with the corpus
// directory overlaid (go build -overlay adds the file to the go:embed
// pattern without touching the checkout). The rebuilt broker runs as its
// own primary in a temp app-data dir: search_howtos and describe_howto need
// only revit_version, so no add-in has to attach to it.
//
// Run on every supported Revit version before a release of the series:
//
//	go test -tags harness ./... -v -run TestHowToEndToEnd -broker-exe ... (remote flags as usual)
package harness_test

import (
	"encoding/json"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/revit/test-harness/mcpclient"
)

// The how-to the test submits: a feature no seed document teaches, small
// enough to run on a blank fixture in one block. ProjectInformation is a
// single element every project has, so the run modifies exactly one element.
// e2eID is the id submit_howto derives from the title (a kebab slug); triage
// keeps it, as it would for a lineage the maintainer does not rename.
const (
	e2eID    = "set-the-project-name-and-number-in-project-information"
	e2eTitle = "Set the project name and number in Project Information"
	e2eTask  = "Read and set the project's Name and Number through Document.ProjectInformation (a ProjectInfo element every project has) inside one Connector.WithTransaction block, and return the values read back after the commit."
	e2eQuery = "set the project name and number"
)

var e2eScript = strings.Join([]string{
	"var doc = Document;",
	"// Goal: set the project's name and number, then read them back to prove the change.",
	"// 1. ProjectInformation is the one ProjectInfo element every project carries; reads need no block.",
	"var info = doc.ProjectInformation;",
	"var before = new { name = info.Name, number = info.Number };",
	"// 2. Writes go inside one WithTransaction block; Name and Number are plain string properties.",
	"Connector.WithTransaction(doc, () => {",
	"    info.Name = \"Corpus Verifier Tower\";",
	"    info.Number = \"E2E-0001\";",
	"});",
	"// 3. Read back after the commit, from the same element.",
	"return new { before, after = new { name = info.Name, number = info.Number } };",
	"",
}, "\n")

type e2eSubmitOut struct {
	Document   json.RawMessage `json:"document"`
	LocalPath  string          `json:"local_path"`
	Verified   *howtoStamp     `json:"verified"`
	Submission *struct {
		ScrubbedDocument json.RawMessage `json:"scrubbed_document"`
		OutboxDocument   string          `json:"outbox_document"`
		IssueBodyPath    string          `json:"issue_body_path"`
		FiledIssueURL    string          `json:"filed_issue_url"`
		Issue            *struct {
			Repo   string   `json:"repo"`
			Title  string   `json:"title"`
			Labels []string `json:"labels"`
		} `json:"issue"`
		NewIssueURL string `json:"new_issue_url"`
	} `json:"submission"`
	Notices []struct {
		Code string `json:"code"`
	} `json:"notices"`
	Guidance string `json:"guidance"`
}

func (o e2eSubmitOut) hasNotice(code string) bool {
	for _, n := range o.Notices {
		if n.Code == code {
			return true
		}
	}
	return false
}

type e2eDescribeOut struct {
	Document *struct {
		ID       string `json:"id"`
		Rev      int    `json:"rev"`
		Script   string `json:"script"`
		Pitfalls []struct {
			Symptom string `json:"symptom"`
		} `json:"pitfalls"`
	} `json:"document"`
	Source       string `json:"source"`
	VerifiedHere bool   `json:"verified_here"`
	Verification *struct {
		Status       string `json:"status"`
		By           string `json:"by"`
		RevitVersion string `json:"revit_version"`
	} `json:"verification"`
	VerifiedOn []string `json:"verified_on"`
}

func callSubmitHowTo(t *testing.T, c *mcpclient.Client, args map[string]any) e2eSubmitOut {
	t.Helper()
	raw, err := c.CallTool("submit_howto", args, 60*time.Second)
	if err != nil {
		t.Fatalf("submit_howto: %v", err)
	}
	return decodeToolResult[e2eSubmitOut](t, raw)
}

func TestHowToEndToEnd(t *testing.T) {
	c, instances := startClient(t)
	inst := instances.Instances[0]
	mainDoc := ""
	for _, d := range inst.Documents {
		if d.Active {
			mainDoc = d.DocumentID
		}
	}
	if mainDoc == "" {
		t.Skip("connected instance has no active document to create fixtures from")
	}
	mcpServerDir, err := filepath.Abs("../mcp-server")
	if err != nil {
		t.Fatal(err)
	}
	realCorpus := filepath.Join(mcpServerDir, "internal", "howto", "corpus")
	if _, err := os.Stat(filepath.Join(realCorpus, "verified.jsonl")); err != nil {
		t.Fatalf("corpus sidecar: %v", err)
	}
	if _, err := os.Stat(filepath.Join(realCorpus, e2eID+".json")); err == nil {
		t.Fatalf("%s already exists in the shared corpus; this test needs an id no seed document has", e2eID)
	}

	// --- 1. The agent session: run the script, then hand it in ------------------------------------
	// execute_script first, on a blank fixture, so the broker has this exact
	// script text on record as succeeded in this session -- that is what
	// earns the submission its session stamp.
	fixtureTitle := createBlankFixtureDocument(t, c, inst.InstanceID, mainDoc)
	fixtureID := ""
	for _, i := range listInstances(t, c).Instances {
		for _, d := range i.Documents {
			if i.InstanceID == inst.InstanceID && d.Title == fixtureTitle {
				fixtureID = d.DocumentID
			}
		}
	}
	if fixtureID == "" {
		t.Fatalf("fixture %s not listed", fixtureTitle)
	}
	run := decodeToolResult[executeScriptOut](t, callExecuteScriptWith(t, c, inst.InstanceID, fixtureID, e2eScript, nil))
	if run.Status != "success" || !strings.Contains(run.ReturnValue, "E2E-0001") {
		t.Fatalf("the how-to's own script must succeed before it is submitted: status=%s %s", run.Status, run.diag())
	}
	if run.Mutations == nil || run.Mutations.Modified != 1 {
		t.Fatalf("expected exactly one modified element (ProjectInfo), got %+v", run.Mutations)
	}

	submitArgs := map[string]any{
		"instance_id": inst.InstanceID,
		"title":       e2eTitle,
		"task":        e2eTask,
		"script":      e2eScript,
		"members":     []string{"Autodesk.Revit.DB.Document.ProjectInformation", "Autodesk.Revit.DB.ProjectInfo.Name", "Autodesk.Revit.DB.ProjectInfo.Number"},
		"pitfalls": []map[string]any{{
			"symptom": "Setting ProjectInfo.Name outside a block throws: the document is read-only until Connector.WithTransaction opens it.",
			"cause":   "Every write, even to a settings-style element like ProjectInfo, is a document modification.",
			"fix":     "Set the properties inside Connector.WithTransaction(doc, () => { ... }).",
		}},
		"queries":            map[string]any{"hit": []map[string]any{{"text": e2eQuery, "tool": "search_howtos", "rank": 1}}},
		"tags":               []string{"project-information", "settings"},
		"confirm_submission": true,
	}
	sub := callSubmitHowTo(t, c, submitArgs)
	t.Cleanup(func() { removeSubmissionFiles(t, sub) })
	if sub.LocalPath == "" || sub.Submission == nil || len(sub.Submission.ScrubbedDocument) == 0 {
		t.Fatalf("submit_howto did not save + prepare: %+v", sub)
	}
	if sub.Verified == nil || sub.Verified.By != "session" || sub.Verified.RevitVersion != inst.RevitVersion || sub.Verified.Status != "passed" {
		t.Fatalf("the exact script ran successfully in this session, so the local document should carry a session stamp for Revit %s; got %+v", inst.RevitVersion, sub.Verified)
	}
	if sub.Submission.FiledIssueURL != "" {
		t.Fatalf("no token is configured for this broker; nothing should have been filed: %s", sub.Submission.FiledIssueURL)
	}
	if sub.Submission.Issue == nil || sub.Submission.NewIssueURL == "" || sub.Submission.IssueBodyPath == "" {
		t.Fatalf("without a token the hand-off must carry the issue fields, the prefilled URL and the body path: %+v", sub.Submission)
	}
	var scrubbed howtoDoc
	if err := json.Unmarshal(sub.Submission.ScrubbedDocument, &scrubbed); err != nil {
		t.Fatal(err)
	}
	if scrubbed.ID != e2eID || scrubbed.Rev != 1 || scrubbed.Script != e2eScript {
		t.Fatalf("scrubbed document: id=%s rev=%d script-unchanged=%v", scrubbed.ID, scrubbed.Rev, scrubbed.Script == e2eScript)
	}

	// --- 2. Triage (mechanical) + 3. sweep + 4. rebuild + 5. search/describe ------------------------
	accepted := acceptSubmission(t, sub.Submission.ScrubbedDocument, "e2e")
	stamp := sweepAndStamp(t, c, inst.InstanceID, mainDoc, inst.RevitVersion, accepted)
	broker := buildOverlayBroker(t, mcpServerDir, realCorpus, e2eID, accepted, []howtoStamp{stamp})
	first := verifyRebuiltBroker(t, broker, inst.RevitVersion, 1, 1)
	if !strings.Contains(first.Document.Script, "ProjectInformation") {
		t.Errorf("describe should return the accepted script")
	}

	// --- 6. A revision of the same lineage goes round again ----------------------------------------
	rev := callSubmitHowTo(t, c, map[string]any{
		"instance_id": inst.InstanceID,
		"id":          e2eID,
		"change_note": "Add the pitfall about Number being a string, not a numeric field.",
		"pitfalls": []map[string]any{{
			"symptom": "Assigning an int to ProjectInfo.Number does not compile (CS0029).",
			"cause":   "Number is a string property; Revit does not interpret it numerically.",
			"fix":     "Assign a string, e.g. info.Number = \"E2E-0001\".",
		}},
		"confirm_submission": true,
	})
	t.Cleanup(func() { removeSubmissionFiles(t, rev) })
	var revised howtoDoc
	if rev.Submission == nil || json.Unmarshal(rev.Submission.ScrubbedDocument, &revised) != nil {
		t.Fatalf("revision was not prepared: %+v", rev)
	}
	if revised.ID != e2eID || revised.Rev != 2 || revised.Script != e2eScript {
		t.Fatalf("an edit by id must produce rev 2 of the same lineage with the script kept: id=%s rev=%d", revised.ID, revised.Rev)
	}
	if rev.hasNotice("unverified-script-change") {
		t.Errorf("the script did not change, so the revision must not be reported as an unverified script change")
	}
	accepted2 := acceptSubmission(t, rev.Submission.ScrubbedDocument, "e2e-rev2")
	stamp2 := sweepAndStamp(t, c, inst.InstanceID, mainDoc, inst.RevitVersion, accepted2)
	// The rev-1 stamp is kept in the sidecar on purpose: the broker must
	// prune it as stale (wrong rev) and report only the rev-2 stamp.
	broker2 := buildOverlayBroker(t, mcpServerDir, realCorpus, e2eID, accepted2, []howtoStamp{stamp, stamp2})
	verifyRebuiltBroker(t, broker2, inst.RevitVersion, 2, 2)
}

// acceptSubmission is the mechanical part of /triage-howto-submission step
// 6: the scrubbed document becomes the lineage's corpus file under
// provenance "submission". The document is kept as raw JSON so the harness
// (which cannot import the broker's howto package) never re-serialises it.
func acceptSubmission(t *testing.T, scrubbed json.RawMessage, ref string) []byte {
	t.Helper()
	var doc map[string]any
	if err := json.Unmarshal(scrubbed, &doc); err != nil {
		t.Fatal(err)
	}
	doc["provenance"] = map[string]any{"kind": "submission", "ref": ref, "reviewed_by": "harness"}
	out, err := json.Marshal(doc)
	if err != nil {
		t.Fatal(err)
	}
	return out
}

// sweepAndStamp runs the accepted document through the corpus sweep's own
// sweepOne on a blank fixture and returns the harness stamp it earned.
func sweepAndStamp(t *testing.T, c *mcpclient.Client, instanceID, mainDoc, revitVersion string, accepted []byte) howtoStamp {
	t.Helper()
	var d howtoDoc
	if err := json.Unmarshal(accepted, &d); err != nil {
		t.Fatal(err)
	}
	if diag := sweepOne(t, c, instanceID, mainDoc, &d); diag != "" {
		t.Fatalf("the accepted document failed its sweep on Revit %s: %s", revitVersion, diag)
	}
	return howtoStamp{ID: d.ID, Rev: d.Rev, ScriptSHA256: scriptSHA256(d.Script), RevitVersion: revitVersion,
		Status: "passed", At: time.Now().UTC().Format(time.RFC3339), By: "harness"}
}

// buildOverlayBroker builds the broker with the real corpus directory plus
// the accepted document and the given stamps appended to the real sidecar,
// via go build -overlay, and returns the binary's path. The checkout is not
// modified.
func buildOverlayBroker(t *testing.T, mcpServerDir, realCorpus, id string, accepted []byte, stamps []howtoStamp) string {
	t.Helper()
	dir := t.TempDir()
	docPath := filepath.Join(dir, id+".json")
	if err := os.WriteFile(docPath, accepted, 0o644); err != nil {
		t.Fatal(err)
	}
	sidecar, err := os.ReadFile(filepath.Join(realCorpus, "verified.jsonl"))
	if err != nil {
		t.Fatal(err)
	}
	var sc []byte
	sc = append(sc, sidecar...)
	if len(sc) > 0 && sc[len(sc)-1] != '\n' {
		sc = append(sc, '\n')
	}
	for _, s := range stamps {
		line, _ := json.Marshal(s)
		sc = append(sc, line...)
		sc = append(sc, '\n')
	}
	sidecarPath := filepath.Join(dir, "verified.jsonl")
	if err := os.WriteFile(sidecarPath, sc, 0o644); err != nil {
		t.Fatal(err)
	}
	overlay := map[string]any{"Replace": map[string]string{
		filepath.Join(realCorpus, id+".json"):       docPath,
		filepath.Join(realCorpus, "verified.jsonl"): sidecarPath,
	}}
	overlayPath := filepath.Join(dir, "overlay.json")
	ob, _ := json.Marshal(overlay)
	if err := os.WriteFile(overlayPath, ob, 0o644); err != nil {
		t.Fatal(err)
	}
	exe := filepath.Join(dir, "mcp-server-e2e")
	if runtime.GOOS == "windows" {
		exe += ".exe"
	}
	build := exec.Command("go", "build", "-overlay", overlayPath, "-ldflags", "-X main.version=e2e", "-o", exe, "./cmd/mcp-server")
	build.Dir = mcpServerDir
	start := time.Now()
	if out, err := build.CombinedOutput(); err != nil {
		t.Fatalf("go build -overlay: %v\n%s", err, out)
	}
	info, err := exec.Command(exe, "-build-info").Output()
	if err != nil {
		t.Fatalf("-build-info: %v", err)
	}
	var bi struct {
		Version     string `json:"version"`
		HowToCorpus *struct {
			Documents  int      `json:"documents"`
			Hash       string   `json:"hash"`
			VerifiedOn []string `json:"verified_on"`
		} `json:"howto_corpus"`
		Err string `json:"howto_corpus_error"`
	}
	if err := json.Unmarshal(info, &bi); err != nil {
		t.Fatalf("-build-info: %v\n%s", err, info)
	}
	if bi.HowToCorpus == nil {
		t.Fatalf("the rebuilt broker cannot load its corpus: %s", bi.Err)
	}
	baseline := len(mustGlob(t, filepath.Join(realCorpus, "*.json")))
	if bi.HowToCorpus.Documents != baseline+1 {
		t.Fatalf("rebuilt broker embeds %d documents, want the %d shared ones plus %s", bi.HowToCorpus.Documents, baseline, id)
	}
	t.Logf("rebuilt broker in %v: %s, %d documents, corpus hash %s, verified on %v", time.Since(start).Round(time.Second), bi.Version, bi.HowToCorpus.Documents, bi.HowToCorpus.Hash, bi.HowToCorpus.VerifiedOn)
	return exe
}

func mustGlob(t *testing.T, pattern string) []string {
	t.Helper()
	m, err := filepath.Glob(pattern)
	if err != nil {
		t.Fatal(err)
	}
	return m
}

// verifyRebuiltBroker starts the rebuilt broker as its own primary in a temp
// app-data dir (no add-in needed: the how-to tools take revit_version) and
// checks that the accepted document is found at rank 1 for its recorded
// query and described with the sweep stamp for this version.
func verifyRebuiltBroker(t *testing.T, exe, revitVersion string, wantRev, wantPitfalls int) e2eDescribeOut {
	t.Helper()
	bc, err := mcpclient.Start(exe, "-mode", "local", "-app-data-dir", t.TempDir())
	if err != nil {
		t.Fatalf("start rebuilt broker: %v", err)
	}
	defer bc.Close()

	raw, err := bc.CallTool("search_howtos", map[string]any{"query": e2eQuery, "revit_version": revitVersion, "top_n": 3}, 60*time.Second)
	if err != nil {
		t.Fatalf("search_howtos: %v", err)
	}
	search := decodeToolResult[howtoSearchOut](t, raw)
	if len(search.Results) == 0 || search.Results[0].ID != e2eID {
		var got []string
		for _, r := range search.Results {
			got = append(got, r.ID)
		}
		t.Fatalf("the accepted document should rank 1 for its recorded query %q on the rebuilt broker (ranker %s); got %v", e2eQuery, search.Ranker, got)
	}
	if !search.Results[0].VerifiedHere || search.Results[0].Source != "seed" {
		t.Errorf("rank-1 hit: verified_here=%v source=%q (want true, seed)", search.Results[0].VerifiedHere, search.Results[0].Source)
	}

	raw, err = bc.CallTool("describe_howto", map[string]any{"id": e2eID, "revit_version": revitVersion}, 30*time.Second)
	if err != nil {
		t.Fatalf("describe_howto: %v", err)
	}
	desc := decodeToolResult[e2eDescribeOut](t, raw)
	if desc.Document == nil || desc.Document.Rev != wantRev || len(desc.Document.Pitfalls) != wantPitfalls {
		t.Fatalf("describe: want rev %d with %d pitfalls, got %+v", wantRev, wantPitfalls, desc.Document)
	}
	if !desc.VerifiedHere || desc.Verification == nil || desc.Verification.By != "harness" || desc.Verification.Status != "passed" || desc.Verification.RevitVersion != revitVersion {
		t.Fatalf("describe on Revit %s: want the sweep's harness stamp, got here=%v %+v", revitVersion, desc.VerifiedHere, desc.Verification)
	}
	if len(desc.VerifiedOn) != 1 || desc.VerifiedOn[0] != revitVersion {
		t.Errorf("a document swept on one version is verified on that version only (a stale rev-1 stamp must be pruned): %v", desc.VerifiedOn)
	}
	return desc
}

// removeSubmissionFiles deletes what submit_howto wrote for this test on the
// broker's machine (the primary runs on this Mac in the project's own
// topology; on a remote broker the paths would not resolve and are left).
func removeSubmissionFiles(t *testing.T, sub e2eSubmitOut) {
	for _, p := range []string{sub.LocalPath} {
		if p != "" {
			os.Remove(p)
		}
	}
	if sub.Submission != nil {
		for _, p := range []string{sub.Submission.OutboxDocument, sub.Submission.IssueBodyPath} {
			if p != "" {
				os.Remove(p)
			}
		}
	}
	// The local sidecar keeps the session stamp lines; they are pruned by
	// every loader once the document is gone, so they are harmless, but a
	// clean directory is what the next run expects.
	if sub.LocalPath != "" {
		sidecar := filepath.Join(filepath.Dir(sub.LocalPath), "verified.jsonl")
		if raw, err := os.ReadFile(sidecar); err == nil {
			var kept []string
			for _, line := range strings.Split(string(raw), "\n") {
				if strings.TrimSpace(line) != "" && !strings.Contains(line, `"id":"`+e2eID+`"`) {
					kept = append(kept, line)
				}
			}
			if len(kept) == 0 {
				os.Remove(sidecar)
			} else {
				os.WriteFile(sidecar, []byte(strings.Join(kept, "\n")+"\n"), 0o644)
			}
		}
	}
}
