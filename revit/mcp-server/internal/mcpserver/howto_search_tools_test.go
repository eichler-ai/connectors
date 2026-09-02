package mcpserver

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/howto"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/howtosearch"
)

// The fixture registry (howToDeps) holds one instance, inst-1, on Revit
// 2025; the embedded seed is verified on 2025 and 2027. Ranking is
// lexical-only here (no models), which is enough to pin the tool contract:
// the version rule, labels, paging, redirects and notices.

func searchDeps(t *testing.T) HowToDeps {
	t.Helper()
	deps := howToDeps(t)
	deps.Search = howtosearch.New(deps.LocalDir, nil, nil, t.Logf)
	return deps
}

func callHowToTool[T any](t *testing.T, cs *mcp.ClientSession, name string, args map[string]any) (T, bool) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{Name: name, Arguments: args})
	if err != nil {
		t.Fatalf("CallTool %s: %v", name, err)
	}
	var out T
	sc, _ := json.Marshal(res.StructuredContent)
	json.Unmarshal(sc, &out)
	return out, res.IsError
}

func TestSearchHowTosRequiresExactlyOneVersionSource(t *testing.T) {
	cs := connectHowToClient(t, searchDeps(t))
	for name, args := range map[string]map[string]any{
		"neither": {"query": "create walls"},
		"both":    {"query": "create walls", "instance_id": "inst-1", "revit_version": "2025"},
	} {
		t.Run(name, func(t *testing.T) {
			out, isErr := callHowToTool[SearchHowTosOut](t, cs, "search_howtos", args)
			if !isErr || out.Error == nil || out.Error.Code != "howto-version-required" {
				t.Fatalf("want howto-version-required, got isErr=%v error=%+v", isErr, out.Error)
			}
			if out.Error.Remedy == nil {
				t.Errorf("the refusal must say how to fix it")
			}
		})
	}
	t.Run("describe too", func(t *testing.T) {
		out, isErr := callHowToTool[DescribeHowToOut](t, cs, "describe_howto", map[string]any{"id": "walls-create-and-join"})
		if !isErr || out.Error == nil || out.Error.Code != "howto-version-required" {
			t.Fatalf("got isErr=%v error=%+v", isErr, out.Error)
		}
	})
	t.Run("malformed version", func(t *testing.T) {
		out, _ := callHowToTool[SearchHowTosOut](t, cs, "search_howtos", map[string]any{"query": "create walls", "revit_version": "R25"})
		if out.Error == nil || out.Error.Code != "howto-version-invalid" {
			t.Fatalf("got %+v", out.Error)
		}
	})
	t.Run("unknown instance", func(t *testing.T) {
		out, _ := callHowToTool[SearchHowTosOut](t, cs, "search_howtos", map[string]any{"query": "create walls", "instance_id": "inst-9"})
		if out.Error == nil || out.Error.Code != "instance-not-found" {
			t.Fatalf("got %+v", out.Error)
		}
	})
}

func TestSearchHowTosResolvesTheInstanceVersionAndLabelsHits(t *testing.T) {
	cs := connectHowToClient(t, searchDeps(t))
	out, isErr := callHowToTool[SearchHowTosOut](t, cs, "search_howtos", map[string]any{
		"query": "enclose a rectangular footprint with walls and confirm the corners join", "instance_id": "inst-1"})
	if isErr || out.Error != nil {
		t.Fatalf("error: %+v", out.Error)
	}
	if out.RevitVersion != "2025" {
		t.Errorf("revit_version = %q, want the instance's 2025", out.RevitVersion)
	}
	if len(out.Results) == 0 || out.Results[0].ID != "walls-create-and-join" {
		t.Fatalf("results = %+v", out.Results)
	}
	top := out.Results[0]
	if !top.VerifiedHere || top.Source != howto.SourceSeed || len(top.VerifiedOn) != 2 {
		t.Errorf("top hit labels: %+v", top)
	}
	if out.Ranker != rankerLexical {
		t.Errorf("ranker = %q", out.Ranker)
	}
	if !strings.Contains(out.Guidance, "verified_here") || !strings.Contains(out.Guidance, "describe_howto") {
		t.Errorf("guidance should explain the label and the next call: %q", out.Guidance)
	}
	if len(out.Results) > defaultHowToTopN {
		t.Errorf("default page is %d, got %d", defaultHowToTopN, len(out.Results))
	}
}

func TestSearchHowTosVersionIsALabelNotAFilter(t *testing.T) {
	cs := connectHowToClient(t, searchDeps(t))
	out, _ := callHowToTool[SearchHowTosOut](t, cs, "search_howtos", map[string]any{
		"query": "create a floor slab from a closed footprint on a level", "revit_version": "2099"})
	if out.Error != nil {
		t.Fatal(out.Error)
	}
	if out.RevitVersion != "2099" || len(out.Results) == 0 {
		t.Fatalf("results for an unverified version must still come back: %+v", out)
	}
	for _, r := range out.Results {
		if r.VerifiedHere {
			t.Errorf("%s claims verified on 2099", r.ID)
		}
	}
	if out.Results[0].ID != "floors-create-from-loop" {
		t.Errorf("rank 1 = %s", out.Results[0].ID)
	}
}

func TestSearchHowTosPagesWithABoundCursor(t *testing.T) {
	cs := connectHowToClient(t, searchDeps(t))
	args := map[string]any{"query": "create walls on a level in the document", "revit_version": "2027", "top_n": 2}
	p1, _ := callHowToTool[SearchHowTosOut](t, cs, "search_howtos", args)
	if p1.Error != nil || len(p1.Results) != 2 || p1.NextCursor == "" || p1.TotalMatched <= 2 {
		t.Fatalf("page 1: %+v", p1)
	}
	args["cursor"] = p1.NextCursor
	p2, _ := callHowToTool[SearchHowTosOut](t, cs, "search_howtos", args)
	if p2.Error != nil || len(p2.Results) == 0 || p2.Results[0].ID == p1.Results[0].ID {
		t.Fatalf("page 2: %+v", p2)
	}
	args["revit_version"] = "2025"
	other, isErr := callHowToTool[SearchHowTosOut](t, cs, "search_howtos", args)
	if !isErr || other.Error == nil || other.Error.Code != "invalid-cursor" {
		t.Fatalf("a cursor is bound to the version it was ranked for: %+v", other.Error)
	}
	if !strings.Contains(other.Error.Message, "revit_version") || other.Error.Source != howtoSource {
		t.Errorf("the refusal should name what changed and come from the how-to tool: %+v", other.Error)
	}
	args["revit_version"] = "2027"
	args["query"] = "something else entirely about sheets"
	bad, isErr := callHowToTool[SearchHowTosOut](t, cs, "search_howtos", args)
	if !isErr || bad.Error == nil || bad.Error.Code != "invalid-cursor" {
		t.Fatalf("a cursor from another query must be refused: %+v", bad.Error)
	}
}

func TestDescribeHowToReturnsTheScriptAndTheVersionsVerification(t *testing.T) {
	cs := connectHowToClient(t, searchDeps(t))
	out, isErr := callHowToTool[DescribeHowToOut](t, cs, "describe_howto", map[string]any{"id": "walls-create-and-join", "instance_id": "inst-1"})
	if isErr || out.Error != nil {
		t.Fatalf("error: %+v", out.Error)
	}
	if out.Document == nil || out.Document.Script == "" || len(out.Document.Pitfalls) == 0 {
		t.Fatalf("document should carry script and pitfalls: %+v", out.Document)
	}
	if out.RevitVersion != "2025" || !out.VerifiedHere || out.Verification == nil || out.Verification.Status != howto.StampPassed || out.Verification.By != howto.ByHarness {
		t.Errorf("verification for 2025: here=%v %+v", out.VerifiedHere, out.Verification)
	}
	if !strings.Contains(out.Guidance, "harness") {
		t.Errorf("guidance should say who verified it: %q", out.Guidance)
	}
	// Provenance is maintainer-facing and must not reach the agent. Checked
	// on the wire bytes, not on the decoded struct (which would drop any
	// unknown field and so could not fail).
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{Name: "describe_howto", Arguments: map[string]any{"id": "walls-create-and-join", "instance_id": "inst-1"}})
	if err != nil {
		t.Fatal(err)
	}
	wire, _ := json.Marshal(res.StructuredContent)
	if !strings.Contains(string(wire), "\"script\"") {
		t.Fatalf("wire shape missing the script: %s", wire)
	}
	for _, hidden := range []string{"provenance", "reviewed_by", "contributors", "\"verify\""} {
		if strings.Contains(string(wire), hidden) {
			t.Errorf("response leaks %s", hidden)
		}
	}
}

func TestDescribeHowToFollowsAbsorbsAndRejectsUnknownIds(t *testing.T) {
	cs := connectHowToClient(t, searchDeps(t))
	out, _ := callHowToTool[DescribeHowToOut](t, cs, "describe_howto", map[string]any{"id": "walls-closed-footprint-confirm-joins", "revit_version": "2027"})
	if out.Error != nil || out.Document == nil || out.Document.ID != "walls-create-and-join" || out.RedirectedFrom != "walls-closed-footprint-confirm-joins" {
		t.Fatalf("redirect: %+v err=%+v", out.Document, out.Error)
	}
	found := false
	for _, n := range out.Notices {
		found = found || n.Code == "howto-redirected"
	}
	if !found {
		t.Errorf("a redirect should be a notice: %+v", out.Notices)
	}
	missing, isErr := callHowToTool[DescribeHowToOut](t, cs, "describe_howto", map[string]any{"id": "no-such-document", "revit_version": "2027"})
	if !isErr || missing.Error == nil || missing.Error.Code != "howto-not-found" {
		t.Fatalf("got %+v", missing.Error)
	}
}

func TestDescribeHowToReportsUnverifiedVersionsAndApiHints(t *testing.T) {
	deps := searchDeps(t)
	c, _, _, _ := howto.Embedded()
	seed, _, _ := c.Get("text-notes-and-annotation-text")
	local := *seed
	local.ID, local.Rev, local.Absorbs = "text-notes-local-variant", 1, nil
	local.APISince = "2026"
	local.Provenance = howto.Provenance{Kind: howto.ProvenanceLocal}
	raw, _ := howto.MarshalDocument(&local)
	os.MkdirAll(deps.LocalDir, 0o755)
	os.WriteFile(filepath.Join(deps.LocalDir, local.ID+".json"), raw, 0o644)
	cs := connectHowToClient(t, deps)

	out, _ := callHowToTool[DescribeHowToOut](t, cs, "describe_howto", map[string]any{"id": local.ID, "instance_id": "inst-1"})
	if out.Error != nil {
		t.Fatal(out.Error)
	}
	if out.VerifiedHere || out.Verification != nil || out.Source != howto.SourceLocal {
		t.Errorf("an unstamped local document: here=%v verification=%+v source=%q", out.VerifiedHere, out.Verification, out.Source)
	}
	if len(out.APIWarnings) != 1 || !strings.Contains(out.APIWarnings[0], "api_since 2026") {
		t.Errorf("api_since 2026 on Revit 2025 should warn: %v", out.APIWarnings)
	}
	if !strings.Contains(out.Guidance, "Not verified on Revit 2025") || !strings.Contains(out.Guidance, "LOCAL") {
		t.Errorf("guidance: %q", out.Guidance)
	}
}

func TestSearchHowTosReportsLocalCorpusProblemsAsNotices(t *testing.T) {
	deps := searchDeps(t)
	os.MkdirAll(deps.LocalDir, 0o755)
	os.WriteFile(filepath.Join(deps.LocalDir, "broken.json"), []byte("{not json"), 0o644)
	os.WriteFile(filepath.Join(deps.LocalDir, howto.SessionSidecarName), []byte("{not a stamp}\n"), 0o644)
	cs := connectHowToClient(t, deps)
	out, isErr := callHowToTool[SearchHowTosOut](t, cs, "search_howtos", map[string]any{"query": "create walls", "revit_version": "2027"})
	if isErr || out.Error != nil {
		t.Fatalf("a broken local file must not fail the search: %+v", out.Error)
	}
	found := false
	for _, n := range out.Notices {
		if n.Code == "howto-local-corpus-problems" {
			found = true
			if !strings.Contains(n.Message, deps.LocalDir) || !strings.Contains(n.Message, "broken.json") || !strings.Contains(n.Message, howto.SessionSidecarName) {
				t.Errorf("notice should name the directory, the bad file and the bad sidecar line: %q", n.Message)
			}
		}
	}
	if !found {
		t.Errorf("expected howto-local-corpus-problems in %+v", out.Notices)
	}
}

func TestHowToToolsWithoutAnIndexSayWhy(t *testing.T) {
	cs := connectHowToClient(t, howToDeps(t))
	out, isErr := callHowToTool[SearchHowTosOut](t, cs, "search_howtos", map[string]any{"query": "create walls", "revit_version": "2027"})
	if !isErr || out.Error == nil || out.Error.Code != "howto-corpus-unavailable" {
		t.Fatalf("got %+v", out.Error)
	}
}
