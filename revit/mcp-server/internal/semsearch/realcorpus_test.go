package semsearch_test

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/semsearch"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/semsearch/staticembed"
)

// TestRealCorpusRecall rebuilds the POC's measurement in Go: the 2027
// discovery corpus (76,601 members) indexed lexically and with the static
// embedder, then the 43 labelled task-style queries scored for recall@k.
// Reference numbers from the Python POC (scratchpad eval_expanded.py /
// eval_static.py, potion-base-8M):
//
//	lexical (BM25F)      recall@1=14/43 @3=20 @10=26
//	hybrid RRF (potion)  recall@1=16/43 @3=22 @10=32
//
// Set SEMSEARCH_POC_DIR to the POC scratch dir (members.json,
// labels_big.json) and SEMSEARCH_POTION_DIR to the model dir; skipped
// otherwise. Floors are a little below the Python numbers: tokenizer and
// BM25 details differ slightly, and the point is to catch a broken port,
// not to pin a single query.
func TestRealCorpusRecall(t *testing.T) {
	poc, model := os.Getenv("SEMSEARCH_POC_DIR"), os.Getenv("SEMSEARCH_POTION_DIR")
	if poc == "" || model == "" {
		t.Skip("SEMSEARCH_POC_DIR / SEMSEARCH_POTION_DIR not set")
	}
	raw, err := os.ReadFile(filepath.Join(poc, "members.json"))
	if err != nil {
		t.Fatal(err)
	}
	var rows []struct {
		Full    string `json:"full"`
		TName   string `json:"tname"`
		MName   string `json:"mname"`
		Kind    string `json:"kind"`
		Summary string `json:"summary"`
	}
	if err := json.Unmarshal(raw, &rows); err != nil {
		t.Fatal(err)
	}
	docs := make([]semsearch.Doc, len(rows))
	for i, r := range rows {
		ns := strings.TrimSuffix(r.Full, "."+r.TName+"."+r.MName)
		docs[i] = semsearch.Doc{MemberID: r.Full, Kind: r.Kind, Namespace: ns, DeclaringType: r.TName, Name: r.MName, Summary: r.Summary, Core: strings.HasPrefix(ns, "Autodesk.")}
	}
	labelsRaw, err := os.ReadFile(filepath.Join(poc, "labels_big.json"))
	if err != nil {
		t.Fatal(err)
	}
	var labels map[string][]string
	if err := json.Unmarshal(labelsRaw, &labels); err != nil {
		t.Fatal(err)
	}

	t0 := time.Now()
	ix := semsearch.Build(docs)
	t.Logf("lexical build: %d docs in %v", ix.Len(), time.Since(t0).Round(time.Millisecond))

	tok, _ := os.ReadFile(filepath.Join(model, "tokenizer.json"))
	st, _ := os.ReadFile(filepath.Join(model, "model.safetensors"))
	emb, err := staticembed.Load(tok, st, true)
	if err != nil {
		t.Fatal(err)
	}
	t0 = time.Now()
	if err := ix.Embed(context.Background(), emb); err != nil {
		t.Fatal(err)
	}
	t.Logf("dense build (3 fields): %v", time.Since(t0).Round(time.Millisecond))

	type metrics struct{ r1, r3, r10, n int }
	eval := func(name string, q func(string) semsearch.Query) metrics {
		var m metrics
		t0 := time.Now()
		for query, answers := range labels {
			hits, err := ix.Search(context.Background(), q(query))
			if err != nil {
				t.Fatal(err)
			}
			want := map[string]bool{}
			for _, a := range answers {
				want[a] = true
			}
			rank := 0
			for i, h := range hits {
				if want[h.Doc.MemberID] {
					rank = i + 1
					break
				}
			}
			m.n++
			if rank == 1 {
				m.r1++
			}
			if rank >= 1 && rank <= 3 {
				m.r3++
			}
			if rank >= 1 && rank <= 10 {
				m.r10++
			}
		}
		t.Logf("%-22s recall@1=%2d/%d @3=%2d @10=%2d  (%v per query)", name, m.r1, m.n, m.r3, m.r10, (time.Since(t0) / time.Duration(m.n)).Round(time.Millisecond))
		return m
	}
	lex := eval("lexical (BM25F)", func(s string) semsearch.Query { return semsearch.Query{Text: s} })
	hyb := eval("hybrid RRF (potion)", func(s string) semsearch.Query { return semsearch.Query{Text: s, Embedder: emb} })

	if lex.r10 < 23 {
		t.Errorf("lexical recall@10 = %d, below the POC-derived floor 23 (Python: 26)", lex.r10)
	}
	if hyb.r10 < 29 {
		t.Errorf("hybrid recall@10 = %d, below the POC-derived floor 29 (Python: 32)", hyb.r10)
	}
	if hyb.r10 <= lex.r10 {
		t.Errorf("hybrid recall@10 (%d) should beat lexical (%d)", hyb.r10, lex.r10)
	}
}
