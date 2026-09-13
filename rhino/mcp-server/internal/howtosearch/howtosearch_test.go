package howtosearch

import (
	"context"
	"testing"
)

// These run lexical-only (nil embedder/reranker) over the real embedded seed
// corpus — no models needed, so they run on CI.

func TestSearchFindsASeedDocument(t *testing.T) {
	svc := New(nil, nil, nil)
	res, err := svc.Search(context.Background(), "add a sphere to the document", "8")
	if err != nil {
		t.Fatalf("search: %v", err)
	}
	if len(res.Hits) == 0 {
		t.Fatal("expected at least one hit for a task that is literally a seed title")
	}
	if res.Status.Documents < 3 {
		t.Errorf("served corpus should hold the seed docs, got %d", res.Status.Documents)
	}
	found := false
	for _, h := range res.Hits {
		if h.Doc.Doc.ID == "add-a-sphere-to-the-document" {
			found = true
		}
	}
	if !found {
		t.Errorf("the sphere how-to should be among the hits for its own task")
	}
}

func TestDescribeByIDAndMiss(t *testing.T) {
	svc := New(nil, nil, nil)
	e, from, _, ok, err := svc.Describe(context.Background(), "read-document-units-and-object-count")
	if err != nil {
		t.Fatal(err)
	}
	if !ok || from != "" {
		t.Fatalf("expected a direct hit, got ok=%v from=%q", ok, from)
	}
	if e.Doc.ScriptLang != "python" {
		t.Errorf("seed docs are python, got %q", e.Doc.ScriptLang)
	}
	if _, _, _, ok, _ := svc.Describe(context.Background(), "no-such-howto"); ok {
		t.Error("a missing id should not resolve")
	}
}

func TestVerifiedOnReflectsTheShippedStamps(t *testing.T) {
	svc := New(nil, nil, nil)
	res, err := svc.Search(context.Background(), "sphere", "8")
	if err != nil {
		t.Fatal(err)
	}
	for _, h := range res.Hits {
		// The seed corpus ships a passing Rhino 8 stamp per doc...
		if !h.Doc.VerifiedOn("8") {
			t.Errorf("%s: the seed corpus is verified on Rhino 8", h.Doc.Doc.ID)
		}
		// ...and nothing claims an unstamped version.
		if h.Doc.VerifiedOn("99") {
			t.Errorf("%s: must not be verified on an unstamped version", h.Doc.Doc.ID)
		}
	}
}
