// Package semsearch is the broker-side ranker behind search_functions
// (PRD §08; design: revit/docs/search-ranking-redesign.md). It replaces the
// add-in's per-Revit-version SQLite/FTS5 ranking with a hybrid pipeline that
// runs entirely in the broker over a corpus the add-in ships once per
// connection:
//
//	tokens  ──► BM25F over name / path / summary ──┐
//	                                               ├─► namespace mask ─► RRF ─► cross-encoder rerank (pool) ─► hits
//	vector  ──► cosine over per-field embeddings ──┘
//
// The pipeline is generic over the document type: a Schema names the text
// fields, their weights and the tie-break, so the same code ranks the API
// member corpus (Doc, APISchema, Build) and the how-to corpus
// (internal/howtosearch), each with its own field set. The encoder and the
// reranker are interfaces so the pipeline is unit-tested with deterministic
// fakes; the real models live in sibling packages.
// Every constant below was chosen by measurement in the POC, and is named
// rather than inlined so the design note's numbers can be traced to code.
package semsearch

import (
	"context"
	"math"
	"sort"
	"strings"
	"sync"
)

// Doc is one API member as the add-in reflects it -- the same fields
// search_functions has always returned (mcpserver.Member), plus Core.
// The JSON tags are the dump_members wire shape (DiscoveryResultMessage.cs),
// so a page decodes straight into []Doc.
type Doc struct {
	MemberID      string `json:"member_id"`
	Kind          string `json:"kind"`
	Namespace     string `json:"namespace"`
	DeclaringType string `json:"declaring_type"`
	Name          string `json:"name"`
	Signature     string `json:"signature"`
	Summary       string `json:"summary"`
	// Core is true for members of RevitAPI.dll/RevitAPIUI.dll, false for any
	// other loaded add-in. Used as a tie-break in favour of core, never to
	// exclude add-ins (PRD §08).
	Core bool `json:"core"`
}

// Path is the declaring type's fully-qualified name. On the wire the add-in
// sends declaring_type already qualified ("Autodesk.Revit.DB.Wall"), so it
// is used as-is; a bare type name is qualified with Namespace.
func (d Doc) Path() string {
	if d.Namespace == "" || strings.HasPrefix(d.DeclaringType, d.Namespace+".") {
		return d.DeclaringType
	}
	return d.Namespace + "." + d.DeclaringType
}

// ShortType is the declaring type without its namespace ("Wall").
func (d Doc) ShortType() string {
	if i := strings.LastIndexByte(d.DeclaringType, '.'); i >= 0 {
		return d.DeclaringType[i+1:]
	}
	return d.DeclaringType
}

// FullName is Path().Name, the shape describe_function accepts.
func (d Doc) FullName() string { return d.Path() + "." + d.Name }

// Embedder turns texts into unit vectors. Implementations must return one
// vector per input, each of length Dim(), L2-normalised (cosine == dot).
type Embedder interface {
	Dim() int
	Embed(ctx context.Context, texts []string) ([][]float32, error)
}

// Reranker scores each candidate text against the query; higher is more
// relevant. Scores need only be comparable within one call.
type Reranker interface {
	Score(ctx context.Context, query string, docs []string) ([]float32, error)
}

// QueryOf is one search over an IndexOf[T].
type QueryOf[T any] struct {
	Text string
	// Mask, when non-nil, is a pre-ranking eligibility test: docs it rejects
	// are excluded before scoring, so scoping never costs relevance (design
	// note §3.4). InNamespace is the API corpus's mask.
	Mask func(T) bool
	// Prefer, when non-nil, is a post-ranking preference: within the head of
	// the ranked list (the rerank pool, RerankPool or DefaultRerankPool) docs
	// it accepts are moved ahead of those it rejects, in their existing
	// relative order. It is a preference, never a filter: a rejected doc is
	// still returned, just later. The window is bounded so a preferred but
	// weak match cannot bury a strong one beyond the first pages: the
	// how-to corpus prefers documents verified on the caller's Revit version.
	Prefer func(T) bool
	// Embedder enables the dense retriever; nil means lexical only. Must be
	// the same embedder the index was built with (IndexOf.Embed).
	Embedder Embedder
	// Reranker, when non-nil, re-scores the top RerankPool fused candidates.
	Reranker   Reranker
	RerankPool int
}

// Query is a search over the API member index.
type Query = QueryOf[Doc]

// InNamespace is the API corpus's mask: exact namespace match, or every doc
// when namespace is empty.
func InNamespace(namespace string) func(Doc) bool {
	if namespace == "" {
		return nil
	}
	return func(d Doc) bool { return d.Namespace == namespace }
}

// HitOf is one ranked result.
type HitOf[T any] struct {
	Doc   T
	Score float64
}

// Hit is one ranked API member.
type Hit = HitOf[Doc]

// Measured constants (design note §3; scratchpad eval_expanded.py /
// eval_pool.py / eval_static.py).
const (
	// candidateDepth is how deep each retriever's list goes before fusion.
	// 93% of labelled targets sit within either retriever's top 200.
	candidateDepth = 200
	// rrfK is the standard reciprocal-rank-fusion smoothing constant.
	rrfK = 60
	// rrfLexicalWeight : rrfDenseWeight = 1.5 : 1 recovered exact name/path
	// matches the dense retriever drops.
	rrfLexicalWeight = 1.5
	rrfDenseWeight   = 1.0
	// DefaultRerankPool: in the POC eval pool 20 delivered the full reranker
	// gain (recall@1 23/43, MRR 0.629, identical to pool 50). Measured pure-Go
	// cost of the shipped int8 cross-encoder on Apple M1 Max: ~1.2-1.3s at
	// pool 20, ~3.3s at pool 50 -- so the larger pool buys nothing at rank 1
	// and costs 2s (crossenc_test.go logs the pool-20 figure on every run).
	DefaultRerankPool = 20
)

// Field is one indexed text field of a Schema: what text it holds and how
// much it weighs on the lexical (BM25F) and dense (cosine) sides. Text is
// used identically by both retrievers so they see one corpus.
type Field[T any] struct {
	Name    string
	Text    func(T) string
	Lexical float64
	Dense   float64
}

// Schema describes a corpus to the pipeline.
type Schema[T any] struct {
	Fields []Field[T]
	// Junk, when non-nil, marks docs dropped at Build so they cost nothing
	// per query and can never be returned.
	Junk func(T) bool
	// Before, when non-nil, breaks exact score ties: true when a should
	// precede b. The original index breaks the remaining ties, so the order
	// is total. It lives inside each retriever so it can only ever act on
	// genuinely equal scores -- never across a real relevance difference.
	Before func(a, b T) bool
	// RerankText is what the cross-encoder reads for a candidate.
	RerankText func(T) string
}

// APISchema is the API member corpus: per-field weights from the POC.
var APISchema = Schema[Doc]{
	Fields: []Field[Doc]{
		{Name: "name", Text: func(d Doc) string { return d.ShortType() + " " + d.Name }, Lexical: 1.0, Dense: 0.6},
		{Name: "path", Text: func(d Doc) string { return d.Namespace }, Lexical: 0.4, Dense: 0.2},
		{Name: "summary", Text: func(d Doc) string { return d.Summary }, Lexical: 0.5, Dense: 1.0},
	},
	Junk: IsJunk,
	// PRD §08: boost core, never exclude add-ins.
	Before:     func(a, b Doc) bool { return a.Core && !b.Core },
	RerankText: RerankText,
}

// IndexOf is a built corpus. Build once per corpus; the dense side is
// attached separately by Embed because it needs a model and the lexical
// side does not.
type IndexOf[T any] struct {
	schema Schema[T]
	docs   []T
	lex    *lexicalIndex

	denseMu sync.RWMutex
	dense   *denseIndex
}

// Index is the API member index. Build once per (Revit version, add-in set).
type Index = IndexOf[Doc]

// Build indexes the non-junk API docs for lexical retrieval.
func Build(docs []Doc) *Index { return BuildWith(APISchema, docs) }

// BuildWith indexes docs under schema for lexical retrieval, dropping the
// ones schema.Junk rejects.
func BuildWith[T any](schema Schema[T], docs []T) *IndexOf[T] {
	kept := make([]T, 0, len(docs))
	for _, d := range docs {
		if schema.Junk == nil || !schema.Junk(d) {
			kept = append(kept, d)
		}
	}
	ix := &IndexOf[T]{schema: schema, docs: kept}
	fields := make([][][]string, len(schema.Fields))
	for f := range fields {
		fields[f] = make([][]string, len(kept))
		for i, d := range kept {
			fields[f][i] = tokenizeField(schema.Fields[f].Text(d))
		}
	}
	ix.lex = newLexicalIndex(fields)
	return ix
}

// Len is the number of indexed (non-junk) docs.
func (ix *IndexOf[T]) Len() int { return len(ix.docs) }

// Docs returns the indexed docs in index order.
func (ix *IndexOf[T]) Docs() []T { return ix.docs }

// Embed attaches the dense retriever by embedding every doc's fields with
// emb. Docs with an empty field get the zero vector there (scores 0).
func (ix *IndexOf[T]) Embed(ctx context.Context, emb Embedder) error {
	texts := make([]func(int) string, len(ix.schema.Fields))
	for f, field := range ix.schema.Fields {
		texts[f] = func(i int) string { return field.Text(ix.docs[i]) }
	}
	d, err := buildDenseIndex(ctx, emb, len(ix.docs), texts)
	if err != nil {
		return err
	}
	ix.denseMu.Lock()
	ix.dense = d
	ix.denseMu.Unlock()
	return nil
}

// HasDense reports whether Embed has completed.
func (ix *IndexOf[T]) HasDense() bool {
	ix.denseMu.RLock()
	defer ix.denseMu.RUnlock()
	return ix.dense != nil
}

func (ix *IndexOf[T]) weights(dense bool) []float64 {
	w := make([]float64, len(ix.schema.Fields))
	for f, field := range ix.schema.Fields {
		if dense {
			w[f] = field.Dense
		} else {
			w[f] = field.Lexical
		}
	}
	return w
}

// Search runs the pipeline and returns up to candidateDepth*2 hits, best
// first. It never returns junk docs or docs q.Mask rejects.
func (ix *IndexOf[T]) Search(ctx context.Context, q QueryOf[T]) ([]HitOf[T], error) {
	tokens := Tokenize(q.Text)
	if len(tokens) == 0 {
		return nil, nil
	}
	mask := ix.mask(q.Mask)

	lexScores := ix.lex.score(tokens, ix.weights(false))
	lexOrder := ix.topIdx(lexScores, mask, candidateDepth)

	fused := lexOrder
	ix.denseMu.RLock()
	dense := ix.dense
	ix.denseMu.RUnlock()
	if q.Embedder != nil && dense != nil {
		qv, err := q.Embedder.Embed(ctx, []string{q.Text})
		if err != nil {
			return nil, err
		}
		denseScores := dense.score(qv[0], ix.weights(true))
		denseOrder := ix.topIdx(denseScores, mask, candidateDepth)
		fused = ix.rrf([][]int{lexOrder, denseOrder}, []float64{rrfLexicalWeight, rrfDenseWeight}, rrfK)
	}
	if len(fused) == 0 {
		return nil, nil
	}

	hits := make([]HitOf[T], len(fused))
	for i, id := range fused {
		// Without a reranker the score is position-derived (1/rank): fused
		// RRF scores are not meaningful to a caller, order is.
		hits[i] = HitOf[T]{Doc: ix.docs[id], Score: 1.0 / float64(i+1)}
	}

	pool := q.RerankPool
	if pool <= 0 {
		pool = DefaultRerankPool
	}
	if pool > len(hits) {
		pool = len(hits)
	}
	if q.Reranker != nil {
		texts := make([]string, pool)
		for i := 0; i < pool; i++ {
			texts[i] = ix.schema.RerankText(hits[i].Doc)
		}
		scores, err := q.Reranker.Score(ctx, q.Text, texts)
		if err != nil {
			return nil, err
		}
		head := hits[:pool]
		order := make([]int, pool)
		for i := range order {
			order[i] = i
		}
		sort.SliceStable(order, func(a, b int) bool { return scores[order[a]] > scores[order[b]] })
		reordered := make([]HitOf[T], pool)
		for i, j := range order {
			reordered[i] = HitOf[T]{Doc: head[j].Doc, Score: float64(scores[j])}
		}
		copy(hits[:pool], reordered)
	}
	if q.Prefer != nil {
		// Stable partition of the head: preferred first, each side in its
		// ranked order. Scores travel with their docs.
		head := hits[:pool]
		sort.SliceStable(head, func(a, b int) bool { return q.Prefer(head[a].Doc) && !q.Prefer(head[b].Doc) })
	}
	return hits, nil
}

// mask returns the per-doc eligibility vector: what accept says, or every
// doc when accept is nil.
func (ix *IndexOf[T]) mask(accept func(T) bool) []bool {
	m := make([]bool, len(ix.docs))
	for i, d := range ix.docs {
		m[i] = accept == nil || accept(d)
	}
	return m
}

// RerankText is what the cross-encoder reads for an API candidate: the
// Type.Member identifier, a callable's parameter list, and its summary. The
// POC's pair format had no parameter list; issue #188 added it because a
// task description names what a call takes ("from a curve array on a level
// with a roof type" is NewFootPrintRoof's parameter list, word for word)
// and the summary alone ("Creates a new FootPrintRoof element.") gave the
// reranker nothing to match, so it placed the method 11th even once the
// keyword bridge had fused it in. Measured on the 43-query label set (full
// pipeline, with tokenizeField's bridge, one corpus construction): no
// parameter list 25/31/35 (recall@1/@3/@10), parameter types only 24/32/35,
// the full list with names 25/33/35 -- so the full list ships. Per query it
// costs "move an element" 1 -> 2, "create a 3d view" 5 -> 9 and "prompt the
// user to pick an element" 5 -> 6, and wins "get an element by its id"
// 2 -> 1, "create a section view" 4 -> 2, "load a family from a file"
// 5 -> 3, "create a sheet" 18 -> 15 and the issue's query 11 -> 1.
// The cross-encoder's tokenizer cuts input from the tail at 512 wordpieces;
// the longest Revit signatures (~10 parameters) plus a summary stay well
// inside that.
func RerankText(d Doc) string {
	s := d.ShortType() + "." + d.Name
	s += paramList(d.Signature)
	if d.Summary != "" {
		s += " — " + d.Summary
	}
	return s
}

// paramList is a signature's first parenthesised parameter list, parentheses
// included, or "" for a property, field or event. Only the FIRST list: a
// writable named indexed property renders two accessor calls
// ("T get_X(A a); void set_X(A a, T value)", #186) and the setter's is not the
// callable shape the reranker should read. Parentheses never occur inside a
// parameter list (generics use angle brackets), so the first ')' ends it.
func paramList(signature string) string {
	i := strings.IndexByte(signature, '(')
	if i < 0 {
		return ""
	}
	j := strings.IndexByte(signature[i:], ')')
	if j < 0 {
		return signature[i:]
	}
	return signature[i : i+j+1]
}

// --- junk --------------------------------------------------------------------

// junkTypes are enum-like types whose members flood keyword matches with
// thousands of near-identical rows (design note §3.1: masking them took
// noise@1 from 8/79 to 0). Matched on the declaring type's full path.
var junkTypes = map[string]bool{
	"Autodesk.Revit.DB.BuiltInFailures":       true,
	"Autodesk.Revit.DB.BuiltInParameter":      true,
	"Autodesk.Revit.DB.BuiltInCategory":       true,
	"Autodesk.Revit.DB.BuiltInParameterGroup": true,
	"Autodesk.Revit.UI.PostableCommand":       true,
}

// IsJunk reports whether d is excluded from the search index. Junk docs
// remain reachable through list_functions and describe_function.
func IsJunk(d Doc) bool {
	if junkTypes[d.Path()] {
		return true
	}
	// Failure-definition types nest under their own namespace
	// (Autodesk.Revit.DB.BuiltInFailures.WallFailures.*), and the POC also
	// masked any ".Failures." path segment.
	return strings.HasPrefix(d.Path(), "Autodesk.Revit.DB.BuiltInFailures.") ||
		strings.Contains(d.Path()+".", ".Failures.")
}

// --- tokenization ------------------------------------------------------------

// SplitIdentifier splits a camelCase / PascalCase / snake_case identifier into
// lower-cased word parts, with the same boundaries as the add-in's
// IdentifierRelevance.SplitWords: "aB", digit<->letter, the tail of a capital
// run before a lowercase letter ("UIApp" -> ui, app), '_' and '`'.
func SplitIdentifier(id string) []string {
	if id == "" {
		return nil
	}
	var words []string
	r := []rune(id)
	start := 0
	flush := func(end int) {
		if end > start {
			words = append(words, strings.ToLower(string(r[start:end])))
		}
	}
	for i := 1; i <= len(r); i++ {
		if i == len(r) {
			flush(i)
			break
		}
		prev, cur := r[i-1], r[i]
		if cur == '_' || cur == '`' {
			flush(i)
			start = i + 1
			i++ // skip the separator itself
			continue
		}
		boundary := (isLower(prev) && isUpper(cur)) ||
			(isDigit(prev) != isDigit(cur)) ||
			(isUpper(prev) && isUpper(cur) && i+1 < len(r) && isLower(r[i+1]))
		if boundary {
			flush(i)
			start = i
		}
	}
	return words
}

// Tokenize turns free text or dotted identifiers into lower-cased tokens:
// split on any non-alphanumeric run, then split each piece as an identifier.
// Single-character free-text words are dropped (the POC kept len>1), but
// sub-word parts from identifier splitting are kept so "IList" and "Level2"
// tokenize the same on both sides of the index.
func Tokenize(text string) []string {
	var out []string
	for _, piece := range strings.FieldsFunc(text, func(c rune) bool {
		return !(isLower(c) || isUpper(c) || isDigit(c) || c == '_' || c == '`')
	}) {
		if len([]rune(piece)) < 2 {
			continue
		}
		out = append(out, SplitIdentifier(piece)...)
	}
	return out
}

// tokenizeField is Tokenize for INDEXED text: it also emits each adjacent
// pair of identifier parts joined, so "FootPrintRoof" indexes as foot, print,
// roof, footprint, printroof. Revit camel-cases compounds inconsistently
// (NewFootPrintRoof next to RoofByFootprint), and people write them as one
// word, so without the bridge "footprint" reached the second and never the
// first (issue #188: Creation.Document.NewFootPrintRoof sat outside the
// keyword pass's top 200 for a query that named it). The query side is
// unchanged: "foot print" in a query already matches the split parts.
// Measured on the 43-query POC label set (full pipeline): recall@1 24 -> 25,
// @3 29 -> 31, @10 34 -> 35; per query, nothing on the first page moved
// down (the bridge-alone table is in docs/search-ranking-redesign.md).
// Unexported on purpose: only BuildWith may widen, the query side stays
// Tokenize. Stated trade-off: the bridged tokens count towards BM25 document
// length, so an n-part name is 2n-1 tokens long where a one-part name stays
// 1; the recall numbers above include that effect. Digits and backtick arity
// bridge too ("List`1" -> list, 1, list1), harmless noise.
func tokenizeField(text string) []string {
	var out []string
	for _, piece := range strings.FieldsFunc(text, func(c rune) bool {
		return !(isLower(c) || isUpper(c) || isDigit(c) || c == '_' || c == '`')
	}) {
		if len([]rune(piece)) < 2 {
			continue
		}
		parts := SplitIdentifier(piece)
		out = append(out, parts...)
		for j := 0; j+1 < len(parts); j++ {
			out = append(out, parts[j]+parts[j+1])
		}
	}
	return out
}

func isLower(c rune) bool { return c >= 'a' && c <= 'z' }
func isUpper(c rune) bool { return c >= 'A' && c <= 'Z' }
func isDigit(c rune) bool { return c >= '0' && c <= '9' }

// --- lexical (BM25F as a weighted sum of per-field BM25) --------------------

const (
	bm25K1 = 1.5
	bm25B  = 0.75
)

type posting struct {
	doc int
	tf  int
}

type bm25Field struct {
	n     int
	avgdl float64
	dl    []int
	post  map[string][]posting
}

type lexicalIndex struct {
	fields []*bm25Field
	n      int
}

func newLexicalIndex(fields [][][]string) *lexicalIndex {
	ix := &lexicalIndex{fields: make([]*bm25Field, len(fields))}
	for f, docs := range fields {
		ix.n = len(docs)
		bf := &bm25Field{n: len(docs), dl: make([]int, len(docs)), post: make(map[string][]posting)}
		total := 0
		for i, toks := range docs {
			bf.dl[i] = len(toks)
			total += len(toks)
			tf := make(map[string]int, len(toks))
			for _, t := range toks {
				tf[t]++
			}
			for t, c := range tf {
				bf.post[t] = append(bf.post[t], posting{doc: i, tf: c})
			}
		}
		if bf.n > 0 {
			bf.avgdl = math.Max(1e-9, float64(total)/float64(bf.n))
		}
		ix.fields[f] = bf
	}
	return ix
}

// score returns one BM25F score per doc for the given query tokens.
func (ix *lexicalIndex) score(tokens []string, weights []float64) []float64 {
	scores := make([]float64, ix.n)
	seen := make(map[string]bool, len(tokens))
	for _, t := range tokens {
		if seen[t] {
			continue
		}
		seen[t] = true
		for f, bf := range ix.fields {
			posts := bf.post[t]
			if len(posts) == 0 {
				continue
			}
			df := float64(len(posts))
			idf := math.Log(1 + (float64(bf.n)-df+0.5)/(df+0.5))
			for _, p := range posts {
				tf := float64(p.tf)
				norm := tf + bm25K1*(1-bm25B+bm25B*float64(bf.dl[p.doc])/bf.avgdl)
				scores[p.doc] += weights[f] * idf * (tf * (bm25K1 + 1)) / norm
			}
		}
	}
	return scores
}

// --- dense -------------------------------------------------------------------

type denseIndex struct {
	dim  int
	vecs [][]float32 // flat n*dim per field; zero rows for empty fields
	n    int
}

// embedBatch bounds one Embed call; static embedders are cheap per call and
// transformer ones want small batches, so this is a modest middle.
const embedBatch = 256

// buildDenseIndex embeds n docs over the given fields; text(f)(i) is doc i's
// text for field f.
func buildDenseIndex(ctx context.Context, emb Embedder, n int, text []func(int) string) (*denseIndex, error) {
	d := &denseIndex{dim: emb.Dim(), n: n, vecs: make([][]float32, len(text))}
	for f := range text {
		d.vecs[f] = make([]float32, n*d.dim)
		// Embed only non-empty texts; empty fields keep the zero vector.
		var idx []int
		var texts []string
		flush := func() error {
			if len(texts) == 0 {
				return nil
			}
			vs, err := emb.Embed(ctx, texts)
			if err != nil {
				return err
			}
			for k, v := range vs {
				copy(d.vecs[f][idx[k]*d.dim:], v)
			}
			idx, texts = idx[:0], texts[:0]
			return nil
		}
		for i := 0; i < n; i++ {
			t := text[f](i)
			if strings.TrimSpace(t) == "" {
				continue
			}
			idx = append(idx, i)
			texts = append(texts, t)
			if len(texts) == embedBatch {
				if err := flush(); err != nil {
					return nil, err
				}
			}
		}
		if err := flush(); err != nil {
			return nil, err
		}
	}
	return d, nil
}

// score returns the weighted cosine score of qv against every doc.
func (d *denseIndex) score(qv []float32, weights []float64) []float64 {
	scores := make([]float64, d.n)
	for f, vecs := range d.vecs {
		w := weights[f]
		for i := 0; i < d.n; i++ {
			row := vecs[i*d.dim : (i+1)*d.dim]
			var dot float32
			for k, x := range row {
				dot += x * qv[k]
			}
			scores[i] += w * float64(dot)
		}
	}
	return scores
}

// --- fusion ------------------------------------------------------------------

// less orders two docs by score, best first; on an exact score tie
// schema.Before decides, and the original index breaks the remaining ties
// so the order is total.
func (ix *IndexOf[T]) less(a, b int, sa, sb float64) bool {
	if sa != sb {
		return sa > sb
	}
	if ix.schema.Before != nil {
		if ix.schema.Before(ix.docs[a], ix.docs[b]) {
			return true
		}
		if ix.schema.Before(ix.docs[b], ix.docs[a]) {
			return false
		}
	}
	return a < b
}

// topIdx returns the indices of the k highest-scoring eligible docs with a
// strictly positive score, best first.
func (ix *IndexOf[T]) topIdx(scores []float64, mask []bool, k int) []int {
	var ids []int
	for i, s := range scores {
		if mask[i] && s > 0 {
			ids = append(ids, i)
		}
	}
	sort.Slice(ids, func(a, b int) bool { return ix.less(ids[a], ids[b], scores[ids[a]], scores[ids[b]]) })
	if len(ids) > k {
		ids = ids[:k]
	}
	return ids
}

// rrf fuses ranked lists by reciprocal rank: score(doc) = Σ w_l / (k + rank_l).
func (ix *IndexOf[T]) rrf(lists [][]int, weights []float64, k int) []int {
	score := make(map[int]float64)
	var order []int
	for l, list := range lists {
		for r, id := range list {
			if _, seen := score[id]; !seen {
				order = append(order, id)
			}
			score[id] += weights[l] / float64(k+r+1)
		}
	}
	sort.Slice(order, func(a, b int) bool { return ix.less(order[a], order[b], score[order[a]], score[order[b]]) })
	return order
}
