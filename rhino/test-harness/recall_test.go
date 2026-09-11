//go:build harness

package harness_test

import (
	"encoding/json"
	"os"
	"sort"
	"strings"
	"testing"
	"time"
)

// recallCase is one labelled query in testdata/recall_corpus.json: a task
// sentence and the single member (a Type.Member / rhinoscriptsyntax.Name
// suffix) that best answers it, tagged by the language the task is phrased for.
type recallCase struct {
	Query    string `json:"query"`
	Member   string `json:"member"`
	Language string `json:"language"`
}

type recallCorpus struct {
	Cases []recallCase `json:"cases"`
}

// recall floors. Deliberately BELOW the numbers measured live (see the corpus
// comment) so a passing run means "the ranker still works end to end", not "a
// specific query is pinned" -- the live corpus shifts with the Rhino version and
// the ranker is fuzzy. A regression that drops recall under these means the
// port broke, which is what this guards. Tuned from the calibration run.
// Measured live on Rhino 8.35: @1=0.42, @3=0.67, @10=0.96. Floors sit a few
// queries below each so ordinary drift (a different Rhino version, the ranker's
// fuzziness) does not fail the run, while a genuine port/ranker regression --
// which tanks all three at once -- does.
const (
	recallFloorAt1  = 0.33
	recallFloorAt3  = 0.55
	recallFloorAt10 = 0.83
)

// TestDiscoveryRecallCorpus is the Rhino equivalent of the Revit connector's
// TestRealCorpusRecall (PRD §09 success criterion): a committed labelled query
// set scored for recall@k against the LIVE semantic index over the real
// RhinoCommon + rhinoscriptsyntax corpus, in both script languages. Unlike the
// servercore measurement (env-gated, an external POC dump), the corpus here is
// committed and the numbers reproduce on any machine with Rhino, because it runs
// the whole wire path -- dump_members paging, the broker index, the semantic
// ranker -- exactly as an agent's search_functions call does.
func TestDiscoveryRecallCorpus(t *testing.T) {
	// The ranker must actually be semantic for this to mean anything; a model-less
	// build cannot produce these numbers, so skip rather than assert a lower bar.
	if !strings.Contains(searchModelsLine(t), "bundled and verified") {
		t.Skip("search models not bundled in this broker; recall is only meaningful with the semantic ranker")
	}

	corpus := loadRecallCorpus(t)

	c := startServer(t)
	waitForInstance(t, c)

	// Wait for the broker's semantic index to finish building (dump_members paging
	// over the whole corpus), the same gate the other discovery cases use.
	deadline := time.Now().Add(150 * time.Second)
	for {
		out, isErr := discCall[searchFunctionsOut](t, c, "search_functions", map[string]any{"query": "add a circle to the document"})
		if isErr {
			t.Fatalf("%+v", out.Error)
		}
		if out.Ranker == "semantic" || out.Ranker == "semantic-no-rerank" {
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("semantic index never became ready (last ranker=%q)", out.Ranker)
		}
		time.Sleep(2 * time.Second)
	}

	const topN = 10
	tally := map[string]*langTally{"python": {}, "csharp": {}}
	var hits1, hits3, hits10 int

	for _, rc := range corpus.Cases {
		lt := tally[rc.Language]
		if lt == nil {
			t.Fatalf("case %q has unknown language %q", rc.Query, rc.Language)
		}
		lt.total++

		out, isErr := discCall[searchFunctionsOut](t, c, "search_functions", map[string]any{"query": rc.Query, "top_n": topN})
		if isErr {
			t.Fatalf("search %q: %+v", rc.Query, out.Error)
		}

		rank := 0 // 1-based rank of the expected member in the results, 0 = not found
		for i, r := range out.Results {
			if strings.HasSuffix(r.DeclaringType+"."+r.Name, rc.Member) {
				rank = i + 1
				break
			}
		}

		switch {
		case rank == 0:
			t.Logf("MISS  [%s] %-48q -> %s (not in top %d)", rc.Language, rc.Query, rc.Member, topN)
		default:
			t.Logf("rank%2d [%s] %-48q -> %s", rank, rc.Language, rc.Query, rc.Member)
		}
		if rank >= 1 && rank <= 1 {
			hits1++
			lt.at1++
		}
		if rank >= 1 && rank <= 3 {
			hits3++
			lt.at3++
		}
		if rank >= 1 && rank <= 10 {
			hits10++
			lt.at10++
		}
	}

	n := len(corpus.Cases)
	r1, r3, r10 := float64(hits1)/float64(n), float64(hits3)/float64(n), float64(hits10)/float64(n)
	t.Logf("recall over %d queries: @1=%d/%d (%.2f)  @3=%d/%d (%.2f)  @10=%d/%d (%.2f)",
		n, hits1, n, r1, hits3, n, r3, hits10, n, r10)
	for _, lang := range sortedKeys(tally) {
		lt := tally[lang]
		t.Logf("  %-7s @1=%d/%d @3=%d/%d @10=%d/%d", lang, lt.at1, lt.total, lt.at3, lt.total, lt.at10, lt.total)
	}

	// Floors catch a broken ranker/port; they are not per-query pins.
	if r1 < recallFloorAt1 {
		t.Errorf("recall@1 %.2f below floor %.2f", r1, recallFloorAt1)
	}
	if r3 < recallFloorAt3 {
		t.Errorf("recall@3 %.2f below floor %.2f", r3, recallFloorAt3)
	}
	if r10 < recallFloorAt10 {
		t.Errorf("recall@10 %.2f below floor %.2f", r10, recallFloorAt10)
	}
}

func loadRecallCorpus(t *testing.T) recallCorpus {
	t.Helper()
	raw, err := os.ReadFile("testdata/recall_corpus.json")
	if err != nil {
		t.Fatalf("read recall corpus: %v", err)
	}
	var corpus recallCorpus
	if err := json.Unmarshal(raw, &corpus); err != nil {
		t.Fatalf("parse recall corpus: %v", err)
	}
	if len(corpus.Cases) == 0 {
		t.Fatal("recall corpus is empty")
	}
	return corpus
}

type langTally struct{ at1, at3, at10, total int }

func sortedKeys(m map[string]*langTally) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}
