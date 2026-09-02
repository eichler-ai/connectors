package mcpserver

import (
	"context"
	"encoding/json"
	"strings"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/discovery"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/registry"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/semsearch"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/semsearch/manager"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/transport"
)

// --- pure helpers ------------------------------------------------------------

func TestSearchCursorRoundTripAndScope(t *testing.T) {
	scope := searchScope("Create Wall", "Autodesk.Revit.DB", "fp", rankerSemantic)
	if scope != searchScope("  create wall ", "Autodesk.Revit.DB", "fp", rankerSemantic) {
		t.Fatal("scope must be case/whitespace insensitive on the query")
	}
	if scope == searchScope("create wall", "", "fp", rankerSemantic) {
		t.Fatal("scope must include the namespace")
	}
	if scope == searchScope("Create Wall", "Autodesk.Revit.DB", "other-fp", rankerSemantic) || scope == searchScope("Create Wall", "Autodesk.Revit.DB", "fp", rankerLexical) {
		t.Fatal("scope must include the ranked set's identity (fingerprint and ranker)")
	}
	c := buildSearchCursor(40, scope)
	off, drec := parseSearchCursor(c, scope, "query and namespace", discoverySource)
	if drec != nil || off != 40 {
		t.Fatalf("parse(%q) = %d, %+v", c, off, drec)
	}
	for _, bad := range []string{"garbage", "-1:" + scope, "x:" + scope, "40:otherscope"} {
		if _, drec := parseSearchCursor(bad, scope, "query and namespace", discoverySource); drec == nil || drec.Code != "invalid-cursor" {
			t.Errorf("parse(%q) should be invalid-cursor, got %+v", bad, drec)
		}
	}
	if off, drec := parseSearchCursor("", scope, "query and namespace", discoverySource); off != 0 || drec != nil {
		t.Fatalf("empty cursor = %d, %+v", off, drec)
	}
}

func TestPageHitsAndTruncation(t *testing.T) {
	long := strings.Repeat("word ", 100) // 500 chars
	hits := make([]semsearch.Hit, 5)
	for i := range hits {
		hits[i] = semsearch.Hit{Doc: semsearch.Doc{MemberID: string(rune('a' + i)), Summary: long}, Score: 1 / float64(i+1)}
	}
	page, next := pageHits(hits, 2, 2)
	if len(page) != 2 || page[0].MemberID != "c" || next != 4 {
		t.Fatalf("page = %+v next = %d", page, next)
	}
	if !strings.HasSuffix(page[0].Summary, "...") || len(page[0].Summary) > maxSummaryChars+3 {
		t.Fatalf("summary not truncated: %d chars", len(page[0].Summary))
	}
	page, next = pageHits(hits, 4, 10)
	if len(page) != 1 || next != 5 {
		t.Fatalf("tail page = %d items, next %d", len(page), next)
	}
	if page, next := pageHits(hits, 99, 10); len(page) != 0 || next != 5 {
		t.Fatalf("past-end page = %d items, next %d", len(page), next)
	}
	if clampTopN(0) != defaultSearchTopN || clampTopN(9999) != maxSearchTopN || clampTopN(7) != 7 {
		t.Fatal("clampTopN")
	}
}

func TestSemanticGuidanceBranches(t *testing.T) {
	empty := semanticGuidance(0, 0, true, true)
	if !strings.Contains(empty, "does not mean the API is absent") || !strings.Contains(empty, "list_functions") {
		t.Errorf("empty guidance = %q", empty)
	}
	dense := semanticGuidance(20, 120, true, true)
	if !strings.Contains(dense, "cross-encoder") || !strings.Contains(dense, "next_cursor") || !strings.Contains(dense, "namespace") {
		t.Errorf("dense guidance = %q", dense)
	}
	lex := semanticGuidance(5, 5, false, false)
	if !strings.Contains(lex, "keyword-only") || strings.Contains(lex, "next_cursor") {
		t.Errorf("lexical guidance = %q", lex)
	}
	noRerank := semanticGuidance(5, 5, true, false)
	if !strings.Contains(noRerank, "reranker is unavailable") || strings.Contains(noRerank, "cross-encoder re-read") {
		t.Errorf("no-rerank guidance = %q", noRerank)
	}
	if rankerName(true, true) != rankerSemantic || rankerName(true, false) != rankerSemanticNoRerank || rankerName(false, false) != rankerLexical {
		t.Error("rankerName mapping")
	}
	if g := fallbackGuidance(manager.Status{State: manager.StateBuilding}); !strings.Contains(g, "still building") {
		t.Errorf("building fallback = %q", g)
	}
}

// --- tool end to end over the fake add-in ------------------------------------

// fakeAddIn answers dump_members from a fixed corpus and search_functions
// with a recognisable keyword-ranker payload, so tests can tell which ranker
// served a call.
func fakeAddIn(t *testing.T, docs []map[string]any, dumpDelay time.Duration) transport.RequestHandler {
	return func(ctx context.Context, method string, params json.RawMessage) (any, *transport.RPCError) {
		switch method {
		case "dump_members":
			if dumpDelay > 0 {
				select {
				case <-time.After(dumpDelay):
				case <-ctx.Done():
					return nil, &transport.RPCError{Code: -32000, Message: "cancelled"}
				}
			}
			var p struct{ Offset, Limit int }
			json.Unmarshal(params, &p)
			end := p.Offset + p.Limit
			if end > len(docs) {
				end = len(docs)
			}
			out := map[string]any{"members": docs[p.Offset:end], "total": len(docs), "fingerprint": "fp-test"}
			if end < len(docs) {
				out["next_offset"] = end
			}
			return out, nil
		case "search_functions":
			return map[string]any{"results": []any{map[string]any{"member_id": "M:FromAddIn", "kind": "Method", "name": "FromAddIn", "signature": "void FromAddIn()", "score": 999}}, "total_matched": 1}, nil
		}
		t.Errorf("unexpected wire method %s", method)
		return nil, &transport.RPCError{Code: -32601, Message: "unknown"}
	}
}

func testCorpus() []map[string]any {
	mk := func(id, typ, name, summary string) map[string]any {
		return map[string]any{"member_id": id, "kind": "Method", "namespace": "Autodesk.Revit.DB", "declaring_type": "Autodesk.Revit.DB." + typ,
			"name": name, "signature": name + "()", "summary": summary, "core": true}
	}
	return []map[string]any{
		mk("M:Autodesk.Revit.DB.Wall.Create", "Wall", "Create", "Creates a new rectangular profile wall within the project."),
		mk("M:Autodesk.Revit.DB.ElementTransformUtils.MoveElement", "ElementTransformUtils", "MoveElement", "Moves one element from its current location by a given transformation."),
		mk("M:Autodesk.Revit.DB.Document.Delete", "Document", "Delete", "Deletes an element from the document."),
		mk("M:Autodesk.Revit.DB.Wall.Flip", "Wall", "Flip", "Flips the wall orientation."),
	}
}

func callSearch(t *testing.T, cs *mcp.ClientSession, args map[string]any) (SearchFunctionsOut, bool) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	res, err := cs.CallTool(ctx, &mcp.CallToolParams{Name: "search_functions", Arguments: args})
	if err != nil {
		t.Fatalf("CallTool: %v", err)
	}
	var out SearchFunctionsOut
	sc, _ := json.Marshal(res.StructuredContent)
	json.Unmarshal(sc, &out)
	return out, res.IsError
}

func TestSearchFunctionsServedFromBrokerIndexWithPaging(t *testing.T) {
	reg := registry.New()
	reg.Register(&registry.Instance{InstanceID: "inst-1", RevitVersion: "2027"}, time.Now())
	r := discovery.NewRouter(reg)
	attachFakeDiscoveryInstance(t, r, "inst-1", fakeAddIn(t, testCorpus(), 0))
	m := manager.New(r, nil, nil, t.Logf)
	m.OnAttach("inst-1")
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if st := m.WaitReady(ctx, "inst-1"); st.State != manager.StateReady {
		t.Fatalf("index state %s (%v)", st.State, st.Err)
	}
	cs := connectDiscoveryClient(t, r, m)

	out, isErr := callSearch(t, cs, map[string]any{"query": "wall", "top_n": 1})
	if isErr {
		t.Fatalf("tool error: %+v", out.Error)
	}
	if out.Ranker != rankerLexical || out.RevitVersion != "2027" {
		t.Fatalf("ranker=%q version=%q", out.Ranker, out.RevitVersion)
	}
	if out.TotalMatched != 2 || len(out.Results) != 1 || out.NextCursor == "" {
		t.Fatalf("page 1 = %+v", out)
	}
	if !strings.HasPrefix(out.Results[0].MemberID, "M:Autodesk.Revit.DB.Wall.") {
		t.Fatalf("top hit = %s", out.Results[0].MemberID)
	}
	if !strings.Contains(out.Guidance, "keyword-only") {
		t.Fatalf("guidance = %q", out.Guidance)
	}

	out2, isErr := callSearch(t, cs, map[string]any{"query": "wall", "top_n": 1, "cursor": out.NextCursor})
	if isErr || len(out2.Results) != 1 || out2.NextCursor != "" || out2.Results[0].MemberID == out.Results[0].MemberID {
		t.Fatalf("page 2 = %+v (err=%v)", out2, isErr)
	}

	// A cursor for a different query is rejected loudly.
	out3, isErr := callSearch(t, cs, map[string]any{"query": "delete element", "cursor": out.NextCursor})
	if !isErr || out3.Error == nil || out3.Error.Code != "invalid-cursor" {
		t.Fatalf("cross-query cursor = %+v", out3)
	}

	// Namespace mask + no-match guidance.
	out4, _ := callSearch(t, cs, map[string]any{"query": "wall", "namespace": "Autodesk.Revit.UI"})
	if out4.TotalMatched != 0 || !strings.Contains(out4.Guidance, "does not mean the API is absent") {
		t.Fatalf("masked search = %+v", out4)
	}
}

func TestSearchFunctionsFallsBackToAddInWhileIndexBuilds(t *testing.T) {
	reg := registry.New()
	reg.Register(&registry.Instance{InstanceID: "inst-1", RevitVersion: "2027"}, time.Now())
	r := discovery.NewRouter(reg)
	// dump_members stalls, so the index stays in StateBuilding for the test.
	attachFakeDiscoveryInstance(t, r, "inst-1", fakeAddIn(t, testCorpus(), 30*time.Second))
	m := manager.New(r, nil, nil, t.Logf)
	m.OnAttach("inst-1")
	cs := connectDiscoveryClient(t, r, m)

	out, isErr := callSearch(t, cs, map[string]any{"query": "Delete"})
	if isErr {
		t.Fatalf("tool error: %+v", out.Error)
	}
	if out.Ranker != rankerKeywordFallback || len(out.Results) != 1 || out.Results[0].MemberID != "M:FromAddIn" {
		t.Fatalf("expected the add-in's ranker to answer: %+v", out)
	}
	if !strings.Contains(out.Guidance, "still building") {
		t.Fatalf("guidance = %q", out.Guidance)
	}
	if len(out.Notices) != 1 || out.Notices[0].Code != "search-index-building" || out.Notices[0].Severity != "info" {
		t.Fatalf("notices = %+v, want one search-index-building info record", out.Notices)
	}
}

func TestSearchFunctionsNoInstanceIsToolErrorOnIndexPath(t *testing.T) {
	r := discovery.NewRouter(registry.New())
	m := manager.New(r, nil, nil, nil)
	cs := connectDiscoveryClient(t, r, m)
	out, isErr := callSearch(t, cs, map[string]any{"query": "wall"})
	if !isErr || out.Error == nil {
		t.Fatalf("expected a tool error with no instance, got %+v", out)
	}
}
