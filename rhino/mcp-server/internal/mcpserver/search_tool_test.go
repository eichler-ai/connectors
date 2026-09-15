package mcpserver

import (
	"strings"
	"testing"

	"github.com/eichler-ai/connectors/internal/servercore/diag"
	"github.com/eichler-ai/connectors/internal/servercore/semsearch"
	"github.com/eichler-ai/connectors/internal/servercore/semsearch/manager"
)

func TestRankerName(t *testing.T) {
	for _, c := range []struct {
		dense, reranked bool
		want            string
	}{
		{true, true, rankerSemantic},
		{true, false, rankerSemanticNoRerank},
		{false, false, rankerLexical},
	} {
		if got := rankerName(c.dense, c.reranked); got != c.want {
			t.Errorf("rankerName(%v,%v) = %q, want %q", c.dense, c.reranked, got, c.want)
		}
	}
}

func TestClampTopN(t *testing.T) {
	if clampTopN(0) != defaultSearchTopN || clampTopN(-5) != defaultSearchTopN {
		t.Fatal("non-positive top_n should clamp to the default, not loop the caller forever")
	}
	if clampTopN(10) != 10 || clampTopN(maxSearchTopN+1) != maxSearchTopN {
		t.Fatal("top_n clamp bounds wrong")
	}
}

func TestTruncateSummary(t *testing.T) {
	long := strings.Repeat("x", maxSummaryChars+50)
	got := truncateSummary(long)
	if len(got) > maxSummaryChars+3 || !strings.HasSuffix(got, "...") {
		t.Fatalf("truncated len=%d suffix=%q", len(got), got[len(got)-3:])
	}
	if truncateSummary("short") != "short" {
		t.Fatal("short summary should pass through")
	}
}

func TestSearchCursorRoundTrip_AndScopeGuard(t *testing.T) {
	scope := searchScope("add a circle", "Rhino.Geometry", "fp1", rankerSemantic)
	cur := buildSearchCursor(20, scope)
	off, drec := parseSearchCursor(cur, scope, "query and namespace", "src")
	if drec != nil || off != 20 {
		t.Fatalf("round-trip off=%d drec=%+v", off, drec)
	}
	// A cursor from a different ranking of the same query must not replay.
	other := searchScope("add a circle", "Rhino.Geometry", "fp1", rankerKeywordFallback)
	if _, drec := parseSearchCursor(cur, other, "query and namespace", "src"); drec == nil || drec.Code != "invalid-cursor" {
		t.Fatalf("expected invalid-cursor across rankings, got %+v", drec)
	}
	if _, drec := parseSearchCursor("garbage", scope, "query and namespace", "src"); drec == nil {
		t.Fatal("malformed cursor should error")
	}
}

func TestPageHits_MapsDocFieldsAndPages(t *testing.T) {
	hits := []semsearch.Hit{
		{Doc: semsearch.Doc{MemberID: "m1", Kind: "core", Namespace: "Rhino.Geometry", DeclaringType: "Sphere", Name: "Radius", Signature: "double Radius", Summary: "r"}, Score: 9},
		{Doc: semsearch.Doc{MemberID: "m2", Kind: "addin", Name: "X"}, Score: 8},
		{Doc: semsearch.Doc{MemberID: "m3"}, Score: 7},
	}
	page, next := pageHits(hits, 0, 2)
	if len(page) != 2 || next != 2 {
		t.Fatalf("page=%d next=%d", len(page), next)
	}
	if page[0].MemberID != "m1" || page[0].Kind != "core" || page[0].Name != "Radius" || page[0].Score != 9 {
		t.Fatalf("mapped[0] = %+v", page[0])
	}
	page2, next2 := pageHits(hits, 2, 2)
	if len(page2) != 1 || next2 != 3 || page2[0].MemberID != "m3" {
		t.Fatalf("page2=%+v next2=%d", page2, next2)
	}
}

func TestSearchGuidanceShape(t *testing.T) {
	if !strings.Contains(searchGuidance(0, 0), "does not mean the API is absent") {
		t.Error("empty guidance should reassure the API is not absent")
	}
	if !strings.Contains(searchGuidance(20, searchManyResults+1), "Narrow rather than widen") {
		t.Error("many-results guidance should advise narrowing")
	}
}

// failedStatus builds a StateFailed status whose build-failure notice carries
// causeCode as its wire cause -- the shape buildFailed produces when
// dump_members returns that record. An empty causeCode means no cause.
func failedStatus(causeCode string) manager.Status {
	rec := diag.New(diag.SeverityWarning, "search-index-build-failed", "test", "the index could not be built")
	if causeCode != "" {
		rec = rec.WithDetail(map[string]any{"cause": diag.New(diag.SeverityError, causeCode, "test", "cause")})
	}
	return manager.Status{State: manager.StateFailed, Err: rec}
}

func TestFallbackGuidance(t *testing.T) {
	building := fallbackGuidance(manager.Status{State: manager.StateBuilding})
	if !strings.Contains(building, "still building") || !strings.Contains(building, "Retry shortly") {
		t.Errorf("building guidance should say it is building and to retry: %q", building)
	}

	// A cold-start race (discovery-unavailable cause) is transient: steer to a
	// retry, and never call it a permanent failure.
	warming := fallbackGuidance(failedStatus(codeDiscoveryUnavailable))
	if !strings.Contains(warming, "warming up") || !strings.Contains(warming, "Retry shortly") {
		t.Errorf("transient failure should steer toward a retry: %q", warming)
	}
	if strings.Contains(warming, "failed to build") {
		t.Errorf("transient failure must not read as a permanent failure: %q", warming)
	}

	// A real, sticky build failure keeps the honest "failed to build" wording.
	failed := fallbackGuidance(failedStatus("some-other-code"))
	if !strings.Contains(failed, "failed to build") {
		t.Errorf("sticky failure should say it failed: %q", failed)
	}
	if strings.Contains(failed, "warming up") {
		t.Errorf("sticky failure must not be dressed up as transient: %q", failed)
	}
	// A failure with no cause record is treated as sticky, not transient.
	if got := fallbackGuidance(failedStatus("")); !strings.Contains(got, "failed to build") {
		t.Errorf("failure with no cause should be sticky: %q", got)
	}
}
