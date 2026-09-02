package semsearch

import (
	"context"
	"math"
	"reflect"
	"strings"
	"testing"
)

// --- tokenization -----------------------------------------------------------

func TestSplitIdentifier(t *testing.T) {
	cases := []struct {
		in   string
		want []string
	}{
		{"createPlaceholder", []string{"create", "placeholder"}},
		{"FilteredElementCollector", []string{"filtered", "element", "collector"}},
		{"UIApplication", []string{"ui", "application"}},        // acronym tail: UI|Application
		{"Level2Plan", []string{"level", "2", "plan"}},          // digit boundaries both sides
		{"get_BoundingBox", []string{"get", "bounding", "box"}}, // underscore
		{"IList`1", []string{"i", "list", "1"}},                 // generic arity backtick
		{"OST_Walls", []string{"ost", "walls"}},                 // all-caps run then underscore
		{"XYZ", []string{"xyz"}},                                // pure acronym stays one token
		{"", nil},
	}
	for _, c := range cases {
		if got := SplitIdentifier(c.in); !reflect.DeepEqual(got, c.want) {
			t.Errorf("SplitIdentifier(%q) = %v, want %v", c.in, got, c.want)
		}
	}
}

func TestTokenizeSplitsFreeTextAndIdentifiers(t *testing.T) {
	got := Tokenize("get every Wall in the document via FilteredElementCollector.OfClass")
	want := []string{"get", "every", "wall", "in", "the", "document", "via", "filtered", "element", "collector", "of", "class"}
	if !reflect.DeepEqual(got, want) {
		t.Errorf("Tokenize = %v, want %v", got, want)
	}
}

func TestTokenizeDropsSingleCharacterNoise(t *testing.T) {
	// The POC's tokenizer keeps only len>1 words, except that identifier
	// splitting may legitimately yield "i" (IList) or a lone digit -- those
	// come from SplitIdentifier and are kept, matching the corpus side.
	got := Tokenize("a wall, a level & a view")
	want := []string{"wall", "level", "view"}
	if !reflect.DeepEqual(got, want) {
		t.Errorf("Tokenize = %v, want %v", got, want)
	}
}

// --- junk filter -------------------------------------------------------------

func TestIsJunkMasksEnumFloodsOnly(t *testing.T) {
	cases := []struct {
		ns, typ, name string
		junk          bool
	}{
		{"Autodesk.Revit.DB", "BuiltInCategory", "OST_Walls", true},
		{"Autodesk.Revit.DB", "BuiltInParameter", "WALL_ATTR_WIDTH_PARAM", true},
		{"Autodesk.Revit.DB", "BuiltInParameterGroup", "PG_GEOMETRY", true},
		{"Autodesk.Revit.DB", "BuiltInFailures", "WallFailures", true},
		{"Autodesk.Revit.DB.BuiltInFailures", "WallFailures", "WallInvalid", true}, // nested failure type
		{"Autodesk.Revit.UI", "PostableCommand", "EditGroup", true},
		{"Autodesk.Revit.DB", "Wall", "Create", false},
		{"Autodesk.Revit.DB", "Category", "GetBuiltInCategory", false}, // member NAME mentioning BuiltIn is fine
		{"Autodesk.Revit.DB", "FailureMessage", "GetSeverity", false},  // "Failure" without ".Failures." segment
	}
	for _, c := range cases {
		d := Doc{Namespace: c.ns, DeclaringType: c.typ, Name: c.name}
		if got := IsJunk(d); got != c.junk {
			t.Errorf("IsJunk(%s.%s.%s) = %v, want %v", c.ns, c.typ, c.name, got, c.junk)
		}
	}
}

// --- fixture corpus ----------------------------------------------------------

func fixture() []Doc {
	return []Doc{
		{MemberID: "M:Autodesk.Revit.DB.Wall.Create", Kind: "Method", Namespace: "Autodesk.Revit.DB", DeclaringType: "Wall", Name: "Create", Summary: "Creates a new rectangular profile wall within the project.", Core: true},
		{MemberID: "M:Autodesk.Revit.DB.ElementTransformUtils.MoveElement", Kind: "Method", Namespace: "Autodesk.Revit.DB", DeclaringType: "ElementTransformUtils", Name: "MoveElement", Summary: "Moves one element from its current location by a given transformation.", Core: true},
		{MemberID: "M:Autodesk.Revit.DB.FilteredElementCollector.OfClass", Kind: "Method", Namespace: "Autodesk.Revit.DB", DeclaringType: "FilteredElementCollector", Name: "OfClass", Summary: "Applies an ElementClassFilter to the collector.", Core: true},
		{MemberID: "P:Autodesk.Revit.DB.Element.Location", Kind: "Property", Namespace: "Autodesk.Revit.DB", DeclaringType: "Element", Name: "Location", Summary: "", Core: true},
		{MemberID: "M:Autodesk.Revit.UI.UIDocument.ShowElements", Kind: "Method", Namespace: "Autodesk.Revit.UI", DeclaringType: "UIDocument", Name: "ShowElements", Summary: "Shows one element in the active view.", Core: true},
		{MemberID: "F:Autodesk.Revit.DB.BuiltInCategory.OST_Walls", Kind: "Field", Namespace: "Autodesk.Revit.DB", DeclaringType: "BuiltInCategory", Name: "OST_Walls", Summary: "Walls", Core: true},
		{MemberID: "M:Acme.Tools.WallHelper.Create", Kind: "Method", Namespace: "Acme.Tools", DeclaringType: "WallHelper", Name: "Create", Summary: "Creates a new rectangular profile wall within the project.", Core: false},
	}
}

func ids(hits []Hit) []string {
	out := make([]string, len(hits))
	for i, h := range hits {
		out[i] = h.Doc.MemberID
	}
	return out
}

func rankOf(hits []Hit, memberID string) int {
	for i, h := range hits {
		if h.Doc.MemberID == memberID {
			return i + 1
		}
	}
	return 0
}

// --- lexical -----------------------------------------------------------------

func TestLexicalRanksNameMatchAboveSummaryOnlyMatch(t *testing.T) {
	ix := Build(fixture())
	hits, err := ix.Search(context.Background(), Query{Text: "move element"})
	if err != nil {
		t.Fatal(err)
	}
	if got := rankOf(hits, "M:Autodesk.Revit.DB.ElementTransformUtils.MoveElement"); got != 1 {
		t.Fatalf("MoveElement rank = %d, want 1; order %v", got, ids(hits))
	}
}

func TestLexicalMasksJunk(t *testing.T) {
	ix := Build(fixture())
	// "walls" is a token of the junk OST_Walls member only (no stemming), so
	// masking it must leave nothing.
	hits, err := ix.Search(context.Background(), Query{Text: "walls"})
	if err != nil {
		t.Fatal(err)
	}
	if len(hits) != 0 {
		t.Fatalf("junk BuiltInCategory member surfaced: %v", ids(hits))
	}
	hits, _ = ix.Search(context.Background(), Query{Text: "wall"})
	if len(hits) == 0 || rankOf(hits, "F:Autodesk.Revit.DB.BuiltInCategory.OST_Walls") != 0 {
		t.Fatalf("expected only non-junk wall members, got %v", ids(hits))
	}
}

func TestCoreWinsTiesOverAddIn(t *testing.T) {
	// Wall.Create and Acme WallHelper.Create have identical name and summary
	// text; the only differences are path tokens and the Core flag. Core must
	// come first (PRD §08: boost core, never exclude add-ins).
	ix := Build(fixture())
	hits, err := ix.Search(context.Background(), Query{Text: "create wall"})
	if err != nil {
		t.Fatal(err)
	}
	core, addin := rankOf(hits, "M:Autodesk.Revit.DB.Wall.Create"), rankOf(hits, "M:Acme.Tools.WallHelper.Create")
	if core == 0 || addin == 0 || core > addin {
		t.Fatalf("core rank %d, add-in rank %d; order %v", core, addin, ids(hits))
	}
}

func TestNamespaceIsAPreRankingMask(t *testing.T) {
	ix := Build(fixture())
	hits, err := ix.Search(context.Background(), Query{Text: "element", Namespace: "Autodesk.Revit.UI"})
	if err != nil {
		t.Fatal(err)
	}
	if len(hits) == 0 {
		t.Fatal("expected UI-namespace results")
	}
	for _, h := range hits {
		if h.Doc.Namespace != "Autodesk.Revit.UI" {
			t.Fatalf("out-of-namespace result leaked: %s", h.Doc.MemberID)
		}
	}
	// Exact namespace only -- no prefix matching that would let
	// "Autodesk.Revit" pull in both DB and UI.
	hits, _ = ix.Search(context.Background(), Query{Text: "element", Namespace: "Autodesk.Revit"})
	if len(hits) != 0 {
		t.Fatalf("namespace prefix should not match: %v", ids(hits))
	}
}

func TestEmptyQueryReturnsNothing(t *testing.T) {
	ix := Build(fixture())
	hits, err := ix.Search(context.Background(), Query{Text: "   "})
	if err != nil {
		t.Fatal(err)
	}
	if len(hits) != 0 {
		t.Fatalf("expected no hits for a blank query, got %v", ids(hits))
	}
}

// --- dense + fusion ----------------------------------------------------------

// keywordEmbedder is a deterministic stand-in for a sentence encoder: each
// text maps to a unit vector over a tiny fixed vocabulary of concepts, so a
// query that shares a *concept* with a doc (but no tokens) still scores.
type keywordEmbedder struct{ concepts map[string]int }

func newKeywordEmbedder() *keywordEmbedder {
	return &keywordEmbedder{concepts: map[string]int{
		"collector": 0, "collect": 0, "all": 0, "every": 0, "filter": 0, "gather": 0,
		"move": 1, "relocate": 1, "translate": 1, "location": 1,
		"wall": 2, "walls": 2,
		"show": 3, "display": 3, "view": 3,
	}}
}

func (k *keywordEmbedder) Dim() int { return 4 }

func (k *keywordEmbedder) Embed(_ context.Context, texts []string) ([][]float32, error) {
	out := make([][]float32, len(texts))
	for i, txt := range texts {
		v := make([]float32, 4)
		for _, tok := range Tokenize(txt) {
			if c, ok := k.concepts[tok]; ok {
				v[c] += 1
			}
		}
		var n float64
		for _, x := range v {
			n += float64(x * x)
		}
		if n > 0 {
			for j := range v {
				v[j] /= float32(math.Sqrt(n))
			}
		}
		out[i] = v
	}
	return out, nil
}

func TestDenseRetrievalSurfacesSemanticMatchLexicalMisses(t *testing.T) {
	ctx := context.Background()
	emb := newKeywordEmbedder()
	ix := Build(fixture())
	if err := ix.Embed(ctx, emb); err != nil {
		t.Fatal(err)
	}
	// "gather all" shares no token with FilteredElementCollector.OfClass or
	// its summary; only the embedder's concept space links them.
	hits, err := ix.Search(ctx, Query{Text: "gather every element", Embedder: emb})
	if err != nil {
		t.Fatal(err)
	}
	if r := rankOf(hits, "M:Autodesk.Revit.DB.FilteredElementCollector.OfClass"); r != 1 {
		t.Fatalf("OfClass rank = %d, want 1; order %v", r, ids(hits))
	}
}

func TestDenseRespectsNamespaceMaskAndJunk(t *testing.T) {
	ctx := context.Background()
	emb := newKeywordEmbedder()
	ix := Build(fixture())
	if err := ix.Embed(ctx, emb); err != nil {
		t.Fatal(err)
	}
	hits, err := ix.Search(ctx, Query{Text: "walls", Embedder: emb})
	if err != nil {
		t.Fatal(err)
	}
	if r := rankOf(hits, "F:Autodesk.Revit.DB.BuiltInCategory.OST_Walls"); r != 0 {
		t.Fatalf("junk leaked through dense path at rank %d", r)
	}
	hits, _ = ix.Search(ctx, Query{Text: "display element", Namespace: "Autodesk.Revit.DB", Embedder: emb})
	for _, h := range hits {
		if h.Doc.Namespace != "Autodesk.Revit.DB" {
			t.Fatalf("dense path leaked out-of-namespace doc %s", h.Doc.MemberID)
		}
	}
}

func TestRRF(t *testing.T) {
	// Two lists, weights 1.5 (lexical) : 1.0 (dense), k=60.
	// doc 7: lex rank 1, dense rank 2 -> 1.5/61 + 1.0/62
	// doc 3: lex rank 2, dense rank 1 -> 1.5/62 + 1.0/61
	// doc 9: dense rank 3 only        -> 1.0/63
	got := rrf([][]int{{7, 3}, {3, 7, 9}}, []float64{1.5, 1.0}, 60)
	want := []int{7, 3, 9}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("rrf order = %v, want %v", got, want)
	}
}

// --- rerank ------------------------------------------------------------------

// fakeReranker scores a candidate by whether it contains the query's last
// word -- enough to prove the reranker reorders the pool and nothing else.
type fakeReranker struct {
	calls    int
	lastPool int
}

func (f *fakeReranker) Score(_ context.Context, query string, docs []string) ([]float32, error) {
	f.calls++
	f.lastPool = len(docs)
	toks := Tokenize(query)
	key := toks[len(toks)-1]
	out := make([]float32, len(docs))
	for i, d := range docs {
		if strings.Contains(strings.ToLower(d), key) {
			out[i] = 1
		}
	}
	return out, nil
}

func TestRerankerReordersOnlyThePool(t *testing.T) {
	ctx := context.Background()
	ix := Build(fixture())
	rr := &fakeReranker{}
	// Lexically "element" hits several docs; the reranker key "shows" should
	// pull UIDocument.ShowElements ("Shows one element...") to rank 1.
	hits, err := ix.Search(ctx, Query{Text: "element shows", Reranker: rr, RerankPool: 2})
	if err != nil {
		t.Fatal(err)
	}
	if rr.calls != 1 || rr.lastPool > 2 {
		t.Fatalf("reranker calls=%d pool=%d, want 1 call over <=2 docs", rr.calls, rr.lastPool)
	}
	// Whatever the pool decided, everything past the pool keeps its fused
	// order -- compare against the no-reranker run.
	base, _ := ix.Search(ctx, Query{Text: "element shows"})
	if len(base) != len(hits) {
		t.Fatalf("reranking changed result count: %d vs %d", len(hits), len(base))
	}
	for i := 2; i < len(base); i++ {
		if base[i].Doc.MemberID != hits[i].Doc.MemberID {
			t.Fatalf("tail reordered at %d: %s vs %s", i, base[i].Doc.MemberID, hits[i].Doc.MemberID)
		}
	}
}

func TestRerankerNotCalledWhenNothingMatched(t *testing.T) {
	ix := Build(fixture())
	rr := &fakeReranker{}
	hits, err := ix.Search(context.Background(), Query{Text: "zzzz qqqq", Reranker: rr, RerankPool: 20})
	if err != nil {
		t.Fatal(err)
	}
	if len(hits) != 0 || rr.calls != 0 {
		t.Fatalf("hits=%d reranker calls=%d, want 0/0", len(hits), rr.calls)
	}
}

func TestRerankText(t *testing.T) {
	d := fixture()[1]
	got := RerankText(d)
	if !strings.Contains(got, "ElementTransformUtils.MoveElement") || !strings.Contains(got, "Moves one element") {
		t.Fatalf("RerankText = %q", got)
	}
	// No summary: no dangling separator.
	if got := RerankText(fixture()[3]); strings.Contains(got, "—") {
		t.Fatalf("RerankText without summary = %q", got)
	}
}
