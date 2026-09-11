// Broker-side search_functions (PRD §09): paging, cursors, display truncation
// and the guidance note for results served from the shared semsearch index
// rather than the plug-in's keyword ranker. The tool registration itself is in
// discovery_tools.go.
package mcpserver

import (
	"fmt"
	"hash/fnv"
	"strconv"
	"strings"

	"github.com/eichler-ai/connectors/internal/servercore/diag"
	"github.com/eichler-ai/connectors/internal/servercore/semsearch"
	"github.com/eichler-ai/connectors/internal/servercore/semsearch/manager"
)

// Broker-owned paging and display bounds for the index path. They match the
// plug-in's values (top_n default 20, max 500; 300-char summaries) so the
// tool's shape did not change when ranking moved broker-side: the corpus
// arrives with untruncated summaries for ranking and is truncated here for
// display.
const (
	defaultSearchTopN = 20
	maxSearchTopN     = 500
	maxSummaryChars   = 300
)

// Ranker values reported on SearchFunctionsOut.Ranker.
const (
	rankerSemantic         = "semantic"           // broker index, dense + lexical + cross-encoder
	rankerSemanticNoRerank = "semantic-no-rerank" // broker index, dense + lexical; cross-encoder unavailable
	rankerLexical          = "lexical"            // broker index, models not bundled in this build
	rankerKeywordFallback  = "keyword-fallback"   // plug-in's own ranker; index not ready
)

// rankerName maps a manager result to the wire value.
func rankerName(dense, reranked bool) string {
	switch {
	case dense && reranked:
		return rankerSemantic
	case dense:
		return rankerSemanticNoRerank
	default:
		return rankerLexical
	}
}

// pageHits turns the ranked hits into one page of wire results.
func pageHits(hits []semsearch.Hit, offset, topN int) ([]Member, int) {
	if offset > len(hits) {
		offset = len(hits)
	}
	end := offset + topN
	if end > len(hits) {
		end = len(hits)
	}
	out := make([]Member, 0, end-offset)
	for _, h := range hits[offset:end] {
		out = append(out, Member{
			MemberID:      h.Doc.MemberID,
			Kind:          h.Doc.Kind,
			Namespace:     h.Doc.Namespace,
			DeclaringType: h.Doc.DeclaringType,
			Name:          h.Doc.Name,
			Signature:     h.Doc.Signature,
			Summary:       truncateSummary(h.Doc.Summary),
			Score:         h.Score,
		})
	}
	return out, end
}

func truncateSummary(s string) string {
	if len(s) <= maxSummaryChars {
		return s
	}
	return strings.TrimRight(s[:maxSummaryChars], " \t\r\n") + "..."
}

// Cursors are "offset:scopehash". The scope covers the query, the namespace and
// the identity of the ranked set (corpus fingerprint plus ranker), so a cursor
// cannot be replayed against another query, nor against a different ranking of
// the same one — e.g. one minted while the plug-in's keyword ranker answered,
// once the broker index is ready.
func searchScope(query, namespace, fingerprint, ranker string) string {
	h := fnv.New32a()
	for _, part := range []string{namespace, strings.ToLower(strings.TrimSpace(query)), fingerprint, ranker} {
		h.Write([]byte(part))
		h.Write([]byte{0})
	}
	return strconv.FormatUint(uint64(h.Sum32()), 16)
}

func buildSearchCursor(offset int, scope string) string {
	return strconv.Itoa(offset) + ":" + scope
}

// parseSearchCursor validates a cursor against the scope this call would mint.
// scopeWords names what the scope covers ("query and namespace") and source is
// the tool's §01 source.
func parseSearchCursor(cursor, scope, scopeWords, source string) (int, *diag.Record) {
	if cursor == "" {
		return 0, nil
	}
	i := strings.IndexByte(cursor, ':')
	if i < 0 {
		return 0, errInvalidCursor(cursor, "not in offset:scope form", scopeWords, source)
	}
	off, err := strconv.Atoi(cursor[:i])
	if err != nil || off < 0 {
		return 0, errInvalidCursor(cursor, "offset is not a non-negative integer", scopeWords, source)
	}
	if cursor[i+1:] != scope {
		return 0, errInvalidCursor(cursor, "issued for a different "+scopeWords+", or for an earlier ranking of the same one", scopeWords, source)
	}
	return off, nil
}

func errInvalidCursor(cursor, why, scopeWords, source string) *diag.Record {
	return diag.New(diag.SeverityError, "invalid-cursor", source,
		fmt.Sprintf("cursor %q is not usable: %s", cursor, why)).
		WithDetail(map[string]any{"cursor": cursor}).
		WithRemedy("re-issue the call with the original " + scopeWords + " and the next_cursor it returned, or drop cursor to start from the first page")
}

func clampTopN(n int) int {
	if n <= 0 {
		return defaultSearchTopN
	}
	if n > maxSearchTopN {
		return maxSearchTopN
	}
	return n
}

// semanticGuidance is the agent-facing note for results served from the broker
// index. It names the mechanism so the agent can shape its next query.
func semanticGuidance(returned, total int, dense, reranked bool) string {
	how := "Ranking fused a keyword pass with a sentence-embedding pass over member names, namespaces and summaries, then a cross-encoder re-read your query against the top candidates."
	switch {
	case !dense:
		how = "Ranking is keyword-only in this build (the embedding models were not bundled), fused over member names, namespaces and summaries."
	case !reranked:
		how = "Ranking fused a keyword pass with a sentence-embedding pass over member names, namespaces and summaries (the cross-encoder reranker is unavailable in this build, so the top of the list is the fused order)."
	}
	if total == 0 {
		return "No members matched -- the query had no words the index recognises. This does not mean the API is absent. " +
			"Describe the task in one plain sentence that names the Rhino type and the operation (e.g. \"add a sphere to the document\"), " +
			"or browse the tree with list_functions."
	}
	tail := " Rank matters more than score: the top of the first page is the best match. If it is not what you want, the target very likely exists under other wording -- " +
		"rephrase as a one-sentence task naming the concrete type (Sphere, Mesh, Curve, Layer, RhinoObject) and the verb, " +
		"drop a likely identifier into the sentence if you suspect one, or scope with namespace (a pre-ranking filter, so it never costs relevance)."
	if total > returned {
		tail += " Further candidates are on next_cursor, in decreasing relevance."
	}
	return how + tail
}

// fallbackGuidance explains why the plug-in's keyword ranker answered instead
// of the broker index, and what to do. The structured reason travels in
// notices[] (fallbackNotice); this is the prose hint beside it.
func fallbackGuidance(st manager.Status) string {
	switch st.State {
	case manager.StateBuilding:
		return "The semantic search index for this Rhino instance is still building (it usually takes a few seconds after the instance connects); this result came from the plug-in's keyword ranker. Retry shortly for semantic ranking. "
	case manager.StateFailed:
		return "The semantic search index for this Rhino instance failed to build (see notices); this result came from the plug-in's keyword ranker, which matches tokens only. "
	default:
		return "The semantic search index is not available for this Rhino instance; this result came from the plug-in's keyword ranker, which matches tokens only. "
	}
}

// fallbackNotice is the §01 record for why the index did not answer: the build
// failure itself when there is one, else an info-level building note.
func fallbackNotice(instanceID string, st manager.Status) *diag.Record {
	if st.State == manager.StateFailed && st.Err != nil {
		return st.Err
	}
	return diag.New(diag.SeverityInfo, "search-index-building", discoverySource,
		"the search_functions index for instance "+instanceID+" is not ready yet ("+st.State.String()+"); this response was ranked by the plug-in's keyword ranker").
		WithDetail(map[string]any{"instance_id": instanceID, "state": st.State.String()}).
		WithRemedy("retry in a few seconds for semantic ranking")
}
