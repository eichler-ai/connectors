// Package crossenc is the cross-encoder reranker behind search_functions: a
// small MS MARCO-trained BERT (cross-encoder/ms-marco-MiniLM-L-6-v2) run
// through hugot's pure-Go GoMLX backend, so the broker stays a single cgo-free
// binary. It re-reads the query against each of the fused top-N candidates
// and is the single largest quality lever in the pipeline (design note §3.2).
//
// Measured (scratchpad spike, then crossenc_test.go which logs the pool-20
// figure on every gated run): the shipped int8 model on Apple M1 Max costs
// ~1.2-1.4s at pool 20 and ~3.3s at pool 50; the fp32 model measured 0.95s /
// 2.1s / 4.3s at pools 20 / 50 / 100 on the M1 Max and 1.0s / 2.4s / 4.8s
// natively in the Windows arm64 guest (4 cores). int8 was not run in the
// guest. Cost scales linearly with the pool, which is why
// semsearch.DefaultRerankPool is 20.
//
// hugot's Go backend uses unsafe pointer arithmetic that the race detector's
// checkptr instrumentation rejects (fatal "pointer arithmetic result points to
// invalid allocation" inside gomlx matmul), so tests that run a model are
// excluded from -race builds with a `!race` constraint; the production
// binary is unaffected.
package crossenc

import (
	"context"
	"fmt"
	"sync"

	"github.com/knights-analytics/hugot"
	"github.com/knights-analytics/hugot/backends"
	"github.com/knights-analytics/hugot/pipelines"
)

// Reranker implements semsearch.Reranker. Safe for concurrent use; hugot
// serialises calls on one pipeline internally, and rerank latency dominates
// anyway, so callers should expect ~1s per query at the default pool.
type Reranker struct {
	session  *hugot.Session
	pipeline *pipelines.CrossEncoderPipeline
	mu       sync.Mutex
}

// batchSize is hugot's inner batch for the pair forward passes; 16 keeps the
// padded batch small for a pool of 20. Not tuned -- no batch-size sweep was
// run.
const batchSize = 16

// Load opens the model directory (model.onnx + tokenizer.json, and the
// special_tokens_map.json/config.json hugot reads for the separator token).
func Load(ctx context.Context, modelDir string) (*Reranker, error) {
	session, err := hugot.NewGoSession(ctx)
	if err != nil {
		return nil, fmt.Errorf("crossenc: session: %w", err)
	}
	p, err := hugot.NewPipeline(session, hugot.CrossEncoderConfig{
		ModelPath: modelDir,
		Name:      "search_functions-reranker",
		Options:   []backends.PipelineOption[*pipelines.CrossEncoderPipeline]{pipelines.WithBatchSize(batchSize)},
	})
	if err != nil {
		_ = session.Destroy()
		return nil, fmt.Errorf("crossenc: load %s: %w", modelDir, err)
	}
	return &Reranker{session: session, pipeline: p}, nil
}

// Score returns one relevance score per doc, in doc order. Scores are the
// sigmoid of the model's logit, so 0..1 and comparable across calls.
func (r *Reranker) Score(ctx context.Context, query string, docs []string) ([]float32, error) {
	if len(docs) == 0 {
		return nil, nil
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	out, err := r.pipeline.RunPipeline(ctx, query, docs)
	if err != nil {
		return nil, fmt.Errorf("crossenc: %w", err)
	}
	scores := make([]float32, len(docs))
	for _, res := range out.Results {
		if res.Index < 0 || res.Index >= len(docs) {
			return nil, fmt.Errorf("crossenc: result index %d outside %d docs", res.Index, len(docs))
		}
		scores[res.Index] = res.Score
	}
	return scores, nil
}

// Close releases the model.
func (r *Reranker) Close() error {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.session == nil {
		return nil
	}
	err := r.session.Destroy()
	r.session, r.pipeline = nil, nil
	return err
}
