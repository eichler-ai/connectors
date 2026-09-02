// Broker-side search_functions (issue #107): paging, cursors, display
// truncation and the guidance note for results served from the
// internal/semsearch index rather than the add-in's keyword ranker. The tool
// registration itself is in discovery_tools.go.
package mcpserver

import (
	"fmt"
	"hash/fnv"
	"strconv"
	"strings"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/semsearch"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/semsearch/manager"
)

const (
	// defaultSearchTopN / maxSearchTopN mirror the add-in's
	// RequestDispatcher (DefaultSearchFunctionsTopN, ClampPageSize).
	defaultSearchTopN = 20
	maxSearchTopN     = 500
	// maxSummaryChars mirrors DiscoveryReflector.MaxSummaryLength: the corpus
	// carries untruncated summaries for ranking; results are truncated for
	// display exactly as the add-in always has.
	maxSummaryChars = 300
)

// Ranker values reported on SearchFunctionsOut.Ranker.
const (
	rankerSemantic        = "semantic"         // broker index, dense + lexical + cross-encoder
	rankerLexical         = "lexical"          // broker index, models not bundled in this build
	rankerKeywordFallback = "keyword-fallback" // add-in's own ranker; index not ready
)

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

// Cursors are "offset:scopehash", the add-in's own scheme, so a cursor
// issued for one query cannot be replayed against another.
func searchScope(query, namespace string) string {
	h := fnv.New32a()
	h.Write([]byte(namespace))
	h.Write([]byte{0})
	h.Write([]byte(strings.ToLower(strings.TrimSpace(query))))
	return strconv.FormatUint(uint64(h.Sum32()), 16)
}

func buildSearchCursor(offset int, scope string) string {
	return strconv.Itoa(offset) + ":" + scope
}

func parseSearchCursor(cursor, scope string) (int, *diag.Record) {
	if cursor == "" {
		return 0, nil
	}
	i := strings.IndexByte(cursor, ':')
	if i < 0 {
		return 0, errInvalidCursor(cursor, "not in offset:scope form")
	}
	off, err := strconv.Atoi(cursor[:i])
	if err != nil || off < 0 {
		return 0, errInvalidCursor(cursor, "offset is not a non-negative integer")
	}
	if cursor[i+1:] != scope {
		return 0, errInvalidCursor(cursor, "issued for a different query or namespace")
	}
	return off, nil
}

func errInvalidCursor(cursor, why string) *diag.Record {
	return diag.New(diag.SeverityError, "invalid-cursor", discoverySource,
		fmt.Sprintf("cursor %q is not usable: %s", cursor, why)).
		WithDetail(map[string]any{"cursor": cursor}).
		WithRemedy("re-issue the call with the original query and namespace and the next_cursor it returned, or drop cursor to start from the first page")
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

// semanticGuidance is the agent-facing note for results served from the
// broker index. It names the mechanism so the agent can shape its next
// query (design note §6/§7).
func semanticGuidance(returned, total int, dense bool) string {
	how := "Ranking fused a keyword pass with a sentence-embedding pass over member names, namespaces and summaries, then a cross-encoder re-read your query against the top candidates."
	if !dense {
		how = "Ranking is keyword-only in this build (the embedding models were not bundled), fused over member names, namespaces and summaries."
	}
	if total == 0 {
		return "No members matched -- the query had no words the index recognises. This does not mean the API is absent. " +
			"Describe the task in one plain sentence that names the Revit element type and the operation (e.g. \"create a wall from a line on a level\"), " +
			"or browse the tree with list_functions."
	}
	tail := " Rank matters more than score: the top of the first page is the best match. If it is not what you want, the target very likely exists under other wording -- " +
		"rephrase as a one-sentence task naming the concrete element type (Wall, View, Parameter, FilteredElementCollector) and the verb, " +
		"drop a likely identifier into the sentence if you suspect one, or scope with namespace (a pre-ranking filter, so it never costs relevance)."
	if total > returned {
		tail += " Further candidates are on next_cursor, in decreasing relevance."
	}
	return how + tail
}

// fallbackGuidance explains why the add-in's keyword ranker answered instead
// of the broker index, and what to do.
func fallbackGuidance(st manager.Status) string {
	switch st.State {
	case manager.StateBuilding:
		return "The semantic search index for this Revit instance is still building (it usually takes a few seconds after the instance connects); this result came from the add-in's keyword ranker. Retry shortly for semantic ranking. "
	case manager.StateFailed:
		return fmt.Sprintf("The semantic search index for this Revit instance failed to build (%v); this result came from the add-in's keyword ranker, which matches tokens only. ", st.Err)
	default:
		return "The semantic search index is not available for this Revit instance; this result came from the add-in's keyword ranker, which matches tokens only. "
	}
}
