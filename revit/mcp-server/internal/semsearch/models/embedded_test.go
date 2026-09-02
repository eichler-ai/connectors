//go:build !race

// See crossenc.go: hugot/GoMLX trips the race detector's checkptr, so model
// inference is not exercised under -race.

package models_test

import (
	"context"
	"testing"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/semsearch/crossenc"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/semsearch/models"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/semsearch/staticembed"
)

// TestEmbeddedModelsLoadThroughTheRealLoaders proves the bundled bytes are
// the models the loaders expect -- a pin can be right for a file the loader
// cannot use. Skipped when the models were not fetched.
func TestEmbeddedModelsLoadThroughTheRealLoaders(t *testing.T) {
	if !models.Available() {
		t.Skip("models not fetched; run fetch-models before building")
	}
	tok, st, normalize, err := models.Embedder()
	if err != nil {
		t.Fatal(err)
	}
	emb, err := staticembed.Load(tok, st, normalize)
	if err != nil {
		t.Fatal(err)
	}
	vs, err := emb.Embed(context.Background(), []string{"move an element to a new location"})
	if err != nil || len(vs) != 1 || len(vs[0]) != emb.Dim() || emb.Dim() != 256 {
		t.Fatalf("embed: %v dim=%d", err, emb.Dim())
	}
	dir, err := models.Materialize(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	rr, err := crossenc.Load(context.Background(), dir)
	if err != nil {
		t.Fatal(err)
	}
	defer rr.Close()
	scores, err := rr.Score(context.Background(), "move an element", []string{"ElementTransformUtils.MoveElement — Moves one element.", "FunctionId.GetObjectData — Serializes."})
	if err != nil || len(scores) != 2 || scores[0] <= scores[1] {
		t.Fatalf("score: %v %v", scores, err)
	}
}
