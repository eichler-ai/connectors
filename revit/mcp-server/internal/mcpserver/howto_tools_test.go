package mcpserver

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/discovery"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/registry"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/transport"
)

func connectHowToClient(t *testing.T, deps HowToDeps) *mcp.ClientSession {
	t.Helper()
	server := mcp.NewServer(&mcp.Implementation{Name: "test", Version: "0"}, nil)
	RegisterHowTo(server, deps)
	ct, st := mcp.NewInMemoryTransports()
	ctx := context.Background()
	if _, err := server.Connect(ctx, st, nil); err != nil {
		t.Fatal(err)
	}
	cs, err := mcp.NewClient(&mcp.Implementation{Name: "c", Version: "0"}, nil).Connect(ctx, ct, nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { cs.Close() })
	return cs
}

func callSubmit(t *testing.T, cs *mcp.ClientSession, args map[string]any) (SubmitHowToOut, bool) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{Name: "submit_howto", Arguments: args})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	var out SubmitHowToOut
	sc, _ := json.Marshal(res.StructuredContent)
	json.Unmarshal(sc, &out)
	return out, res.IsError
}

func goodArgs() map[string]any {
	return map[string]any{
		"title":   "Tag every door on a level",
		"task":    "Place a door tag on every door instance hosted on a given level using IndependentTag.Create in the level's plan view.",
		"script":  "var doc = Document;\nreturn Connector.WithTransaction(doc, () => { return 1; });\n",
		"members": []string{"Autodesk.Revit.DB.IndependentTag.Create"},
		"pitfalls": []map[string]any{{"symptom": "IndependentTag.Create throws when the view is not a plan view",
			"cause": "door tags need a plan view of the door's level", "fix": "look up the ViewPlan for the level first"}},
		"tags": []string{"tags", "doors"},
	}
}

func howToDeps(t *testing.T) HowToDeps {
	t.Helper()
	reg := registry.New()
	reg.Register(&registry.Instance{InstanceID: "inst-1", RevitVersion: "2025",
		Documents: []registry.Document{{ID: "doc-1", Title: "Tower B Coordination", Active: true}}}, time.Now())
	r := discovery.NewRouter(reg)
	attachFakeDiscoveryInstance(t, r, "inst-1", func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		return map[string]any{}, nil
	})
	root := filepath.Join(t.TempDir(), "howto")
	return HowToDeps{LocalDir: filepath.Join(root, "local"), OutboxDir: filepath.Join(root, "outbox"), Registry: reg, Router: r, Version: "dev", RepoSlug: "eichler-ai/connectors"}
}

func TestSubmitHowToInvalidIsRefusedWithProblems(t *testing.T) {
	cs := connectHowToClient(t, howToDeps(t))
	args := goodArgs()
	args["title"] = "short"
	args["members"] = []string{"Wall.Create"}
	out, isErr := callSubmit(t, cs, args)
	if !isErr || out.Error == nil || out.Error.Code != "howto-invalid" {
		t.Fatalf("expected howto-invalid, got %+v", out)
	}
	problems, _ := out.Error.Detail["problems"].([]any)
	if len(problems) < 2 || len(out.Error.Remedy) == 0 {
		t.Fatalf("problems=%v remedy=%q", problems, out.Error.Remedy)
	}
}

func TestSubmitHowToSavesLocallyAndGatesTheSubmission(t *testing.T) {
	deps := howToDeps(t)
	cs := connectHowToClient(t, deps)
	out, isErr := callSubmit(t, cs, goodArgs())
	if isErr {
		t.Fatalf("tool error: %+v", out.Error)
	}
	if out.Document == nil || out.Document.ID != "tag-every-door-on-a-level" || out.Submission != nil {
		t.Fatalf("out = %+v", out)
	}
	if _, err := os.Stat(out.LocalPath); err != nil {
		t.Fatalf("local file missing: %v", err)
	}
	codes := map[string]bool{}
	for _, n := range out.Notices {
		codes[n.Code] = true
	}
	if !codes["howto-submission-confirmation-required"] || !codes["howto-script-not-run-this-session"] {
		t.Fatalf("notices = %+v", out.Notices)
	}
	if !strings.Contains(out.Guidance, "Review the document") {
		t.Fatalf("guidance = %q", out.Guidance)
	}
}

func TestSubmitHowToWithConfirmationPreparesScrubbedIssue(t *testing.T) {
	deps := howToDeps(t)
	cs := connectHowToClient(t, deps)
	args := goodArgs()
	args["task"] = "Tag doors in Tower B Coordination from C:\\Projects\\x.rvt."
	args["confirm_submission"] = true
	args["credit_as"] = "nick"
	out, isErr := callSubmit(t, cs, args)
	if isErr {
		t.Fatalf("tool error: %+v", out.Error)
	}
	if out.Submission == nil {
		t.Fatalf("no submission block: %+v", out)
	}
	s := out.Submission
	if strings.Contains(s.ScrubbedDocument.Task, "Tower B") || strings.Contains(s.ScrubbedDocument.Task, "C:\\") {
		t.Fatalf("not scrubbed: %q", s.ScrubbedDocument.Task)
	}
	if !strings.Contains(s.ScrubbedDocument.Task, "<document>") {
		t.Fatalf("document title not replaced: %q", s.ScrubbedDocument.Task)
	}
	if len(s.ScrubbedDocument.Contributors) != 1 || s.ScrubbedDocument.Contributors[0].Handle != "nick" {
		t.Fatalf("credit = %+v", s.ScrubbedDocument.Contributors)
	}
	for _, p := range []string{s.OutboxDocument, s.IssueBodyPath} {
		if _, err := os.Stat(p); err != nil {
			t.Fatalf("outbox file missing: %v", err)
		}
	}
	if s.FiledIssueURL != "" || s.Issue == nil || s.Issue.Repo != "eichler-ai/connectors" || s.Issue.Title == "" || !strings.Contains(s.Issue.Body, "```json") || len(s.Issue.Labels) != 1 || s.NewIssueURL == "" {
		t.Fatalf("without a token the hand-off must be the issue fields: %+v", s)
	}
	if strings.Contains(s.Issue.Body, "Tower B") || !strings.Contains(out.Guidance, "GitHub connector") || !strings.Contains(out.Guidance, "new_issue_url") {
		t.Fatalf("issue body / guidance: %q", out.Guidance)
	}
	// The unscrubbed local copy keeps the user's own text (it never leaves the machine).
	raw, _ := os.ReadFile(out.LocalPath)
	if !strings.Contains(string(raw), "Tower B") {
		t.Fatal("the local copy must not be scrubbed")
	}
}

func TestSubmitHowToImprovesAnExistingLocalDocument(t *testing.T) {
	deps := howToDeps(t)
	cs := connectHowToClient(t, deps)
	first, _ := callSubmit(t, cs, goodArgs())
	out, isErr := callSubmit(t, cs, map[string]any{"id": first.Document.ID, "change_note": "add a pitfall",
		"pitfalls": []map[string]any{{"symptom": "Tags land at the door origin, not the centre", "cause": "tag point is the origin", "fix": "offset by half the width"}}})
	if isErr {
		t.Fatalf("tool error: %+v", out.Error)
	}
	if out.Document.Rev != 2 || len(out.Document.Pitfalls) != 2 {
		t.Fatalf("revision = %+v", out.Document)
	}
	if _, isErr := callSubmit(t, cs, map[string]any{"id": first.Document.ID}); !isErr {
		t.Fatal("edit without change_note accepted")
	}
}

func TestSubmitHowToWithNoInstanceSavesUnstamped(t *testing.T) {
	deps := howToDeps(t)
	deps.Registry = registry.New()
	deps.Router = discovery.NewRouter(deps.Registry)
	cs := connectHowToClient(t, deps)
	out, isErr := callSubmit(t, cs, goodArgs())
	if isErr || out.Document == nil || out.Verified != nil {
		t.Fatalf("no-instance save: err=%v out=%+v", isErr, out)
	}
}

func TestSubmitHowToFilesTheIssueWhenATokenIsConfigured(t *testing.T) {
	deps := howToDeps(t)
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusCreated)
		fmt.Fprint(w, `{"html_url":"https://github.com/eichler-ai/connectors/issues/172","number":172,"labels":[]}`)
	}))
	defer srv.Close()
	deps.GitHubToken, deps.HTTPClient, deps.GitHubAPI = "ghp_test", srv.Client(), srv.URL
	cs := connectHowToClient(t, deps)
	args := goodArgs()
	args["confirm_submission"] = true
	out, isErr := callSubmit(t, cs, args)
	if isErr {
		t.Fatalf("tool error: %+v", out.Error)
	}
	if out.Submission == nil || out.Submission.FiledIssueURL == "" || out.Submission.FiledIssueNum != 172 || out.Submission.Issue != nil {
		t.Fatalf("not filed: %+v", out.Submission)
	}
	codes := map[string]bool{}
	for _, n := range out.Notices {
		codes[n.Code] = true
	}
	if !codes["howto-issue-unlabelled"] {
		t.Fatalf("an unlabelled filing must be reported: %+v", out.Notices)
	}
	// Without confirmation nothing is posted even with a token.
	deps2 := deps
	hits := 0
	srv2 := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { hits++; w.WriteHeader(201); fmt.Fprint(w, `{}`) }))
	defer srv2.Close()
	deps2.HTTPClient, deps2.GitHubAPI = srv2.Client(), srv2.URL
	cs2 := connectHowToClient(t, deps2)
	if out, _ := callSubmit(t, cs2, goodArgs()); out.Submission != nil || hits != 0 {
		t.Fatalf("gate bypassed: submission=%v hits=%d", out.Submission, hits)
	}
}
