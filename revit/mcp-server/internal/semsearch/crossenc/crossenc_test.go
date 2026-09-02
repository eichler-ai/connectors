package crossenc

import (
	"context"
	"os"
	"testing"
	"time"
)

// TestScoresMatchPythonCrossEncoder pins the Go pipeline to the reference
// sentence-transformers CrossEncoder on three pairs. Python logits for
// cross-encoder/ms-marco-MiniLM-L-6-v2 (measured in the POC venv):
//
//	MoveElement doc   5.8358 -> sigmoid 0.9971
//	Location.Move doc 6.6220 -> sigmoid 0.9987
//	GetObjectData doc -11.4362 -> sigmoid 0.0000
//
// The int8 model drifts slightly from fp32, so the check is ordering plus a
// loose band, not equality. Needs SEMSEARCH_MSMARCO_DIR; skipped otherwise.
func TestScoresMatchPythonCrossEncoder(t *testing.T) {
	dir := os.Getenv("SEMSEARCH_MSMARCO_DIR")
	if dir == "" {
		t.Skip("SEMSEARCH_MSMARCO_DIR not set")
	}
	ctx := context.Background()
	r, err := Load(ctx, dir)
	if err != nil {
		t.Fatal(err)
	}
	defer r.Close()

	q := "move an element to a new location"
	docs := []string{
		"ElementTransformUtils.MoveElement — Moves one element from its current location by a given transformation.",
		"Location.Move — Move the element from its current location by a given translation vector.",
		"FunctionId.GetObjectData — Retrieves data needed to serialize the target object.",
	}
	t0 := time.Now()
	scores, err := r.Score(ctx, q, docs)
	if err != nil {
		t.Fatal(err)
	}
	t.Logf("scores %.4f (first call incl. graph compile %v)", scores, time.Since(t0).Round(time.Millisecond))
	if !(scores[1] > scores[0] && scores[0] > scores[2]) {
		t.Fatalf("ordering wrong: %v", scores)
	}
	if scores[0] < 0.9 || scores[1] < 0.9 || scores[2] > 0.05 {
		t.Fatalf("scores outside the reference band: %v", scores)
	}

	// Empty input is a no-op, not a model call.
	if s, err := r.Score(ctx, q, nil); err != nil || s != nil {
		t.Fatalf("empty docs: %v %v", s, err)
	}

	// Latency at the default pool, warm.
	pool := make([]string, 20)
	for i := range pool {
		pool[i] = docs[i%3]
	}
	t0 = time.Now()
	if _, err := r.Score(ctx, q, pool); err != nil {
		t.Fatal(err)
	}
	el := time.Since(t0)
	t.Logf("pool=20 warm: %v", el.Round(time.Millisecond))
	if el > 10*time.Second {
		t.Errorf("pool-20 rerank took %v; the budget assumed in semsearch.DefaultRerankPool is ~1-3s", el)
	}
}
