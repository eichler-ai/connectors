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
// The encoder and the reranker are interfaces so the pipeline is unit-tested
// with deterministic fakes; the real models live in sibling packages.
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
type Doc struct {
	MemberID      string
	Kind          string
	Namespace     string
	DeclaringType string
	Name          string
	Signature     string
	Summary       string
	// Core is true for members of RevitAPI.dll/RevitAPIUI.dll, false for any
	// other loaded add-in. Used as a tie-break in favour of core, never to
	// exclude add-ins (PRD §08).
	Core bool
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

// Query is one search_functions call.
type Query struct {
	Text string
	// Namespace, when non-empty, is an exact-match pre-ranking mask: docs in
	// any other namespace are excluded before scoring, so scoping never costs
	// relevance (design note §3.4).
	Namespace string
	// Embedder enables the dense retriever; nil means lexical only. Must be
	// the same embedder the index was built with (Index.Embed).
	Embedder Embedder
	// Reranker, when non-nil, re-scores the top RerankPool fused candidates.
	Reranker   Reranker
	RerankPool int
}

// Hit is one ranked result.
type Hit struct {
	Doc   Doc
	Score float64
}

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
	// DefaultRerankPool: pool 20 delivered the full reranker gain
	// (recall@1 23/43, MRR 0.629 == pool 50) at ~1s in pure Go; pool 50
	// costs 2.1-2.4s for no rank-1 gain.
	DefaultRerankPool = 20
	// coreTieBreak is far below any real score difference; it only orders
	// otherwise-identical core vs add-in members.
	coreTieBreak = 1e-9
)

// Per-field weights, lexical (BM25F) and dense (cosine), from the POC.
var (
	lexicalFieldWeights = [numFields]float64{fieldName: 1.0, fieldPath: 0.4, fieldDesc: 0.5}
	denseFieldWeights   = [numFields]float64{fieldName: 0.6, fieldPath: 0.2, fieldDesc: 1.0}
)

const (
	fieldName = iota
	fieldPath
	fieldDesc
	numFields
)

// fieldText is the text indexed for each field, used identically by the
// lexical and dense sides so the two retrievers see one corpus.
func fieldText(d Doc, field int) string {
	switch field {
	case fieldName:
		return d.ShortType() + " " + d.Name
	case fieldPath:
		return d.Namespace
	default:
		return d.Summary
	}
}

// Index is a built corpus. Build once per (Revit version, add-in set); the
// dense side is attached separately by Embed because it needs a model and
// the lexical side does not.
type Index struct {
	docs []Doc
	junk []bool
	lex  *lexicalIndex

	denseMu sync.RWMutex
	dense   *denseIndex
}

// Build indexes docs for lexical retrieval.
func Build(docs []Doc) *Index {
	ix := &Index{docs: docs, junk: make([]bool, len(docs))}
	fields := make([][][]string, numFields)
	for f := range fields {
		fields[f] = make([][]string, len(docs))
	}
	for i, d := range docs {
		ix.junk[i] = IsJunk(d)
		for f := 0; f < numFields; f++ {
			fields[f][i] = Tokenize(fieldText(d, f))
		}
	}
	ix.lex = newLexicalIndex(fields)
	return ix
}

// Len is the number of indexed docs, junk included.
func (ix *Index) Len() int { return len(ix.docs) }

// Embed attaches the dense retriever by embedding every doc's fields with
// emb. Docs with an empty field get the zero vector there (scores 0).
func (ix *Index) Embed(ctx context.Context, emb Embedder) error {
	d, err := buildDenseIndex(ctx, emb, ix.docs)
	if err != nil {
		return err
	}
	ix.denseMu.Lock()
	ix.dense = d
	ix.denseMu.Unlock()
	return nil
}

// HasDense reports whether Embed has completed.
func (ix *Index) HasDense() bool {
	ix.denseMu.RLock()
	defer ix.denseMu.RUnlock()
	return ix.dense != nil
}

// Search runs the pipeline and returns up to candidateDepth*2 hits, best
// first. It never returns junk docs or docs outside q.Namespace.
func (ix *Index) Search(ctx context.Context, q Query) ([]Hit, error) {
	tokens := Tokenize(q.Text)
	if len(tokens) == 0 {
		return nil, nil
	}
	mask := ix.mask(q.Namespace)

	lexScores := ix.lex.score(tokens, lexicalFieldWeights)
	lexOrder := topIdx(lexScores, mask, candidateDepth)

	fused := lexOrder
	ix.denseMu.RLock()
	dense := ix.dense
	ix.denseMu.RUnlock()
	if q.Embedder != nil && dense != nil {
		qv, err := q.Embedder.Embed(ctx, []string{q.Text})
		if err != nil {
			return nil, err
		}
		denseScores := dense.score(qv[0], denseFieldWeights)
		denseOrder := topIdx(denseScores, mask, candidateDepth)
		fused = rrf([][]int{lexOrder, denseOrder}, []float64{rrfLexicalWeight, rrfDenseWeight}, rrfK)
	}
	if len(fused) == 0 {
		return nil, nil
	}

	hits := make([]Hit, len(fused))
	for i, id := range fused {
		// Position-derived score keeps hits strictly ordered and lets core
		// win an exact tie without disturbing any real ranking signal.
		hits[i] = Hit{Doc: ix.docs[id], Score: 1.0 / float64(i+1)}
	}
	hits = ix.breakCoreTies(hits, fused, lexScores)

	if q.Reranker != nil {
		pool := q.RerankPool
		if pool <= 0 {
			pool = DefaultRerankPool
		}
		if pool > len(hits) {
			pool = len(hits)
		}
		texts := make([]string, pool)
		for i := 0; i < pool; i++ {
			texts[i] = RerankText(hits[i].Doc)
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
		reordered := make([]Hit, pool)
		for i, j := range order {
			reordered[i] = Hit{Doc: head[j].Doc, Score: float64(scores[j])}
		}
		copy(hits[:pool], reordered)
	}
	return hits, nil
}

// mask returns the per-doc eligibility vector for q: not junk, and in the
// requested namespace (exact match) when one is given.
func (ix *Index) mask(namespace string) []bool {
	m := make([]bool, len(ix.docs))
	for i, d := range ix.docs {
		m[i] = !ix.junk[i] && (namespace == "" || d.Namespace == namespace)
	}
	return m
}

// breakCoreTies reorders adjacent hits whose lexical scores are equal so a
// core member precedes an add-in one. Only exact lexical ties are touched,
// so this cannot override a real relevance difference.
func (ix *Index) breakCoreTies(hits []Hit, ids []int, lexScores []float64) []Hit {
	sort.SliceStable(hits, func(a, b int) bool {
		sa, sb := lexScores[ids[a]], lexScores[ids[b]]
		if sa != sb {
			return false // keep fused order
		}
		return hits[a].Doc.Core && !hits[b].Doc.Core
	})
	return hits
}

// RerankText is what the cross-encoder reads for a candidate: the
// Type.Member identifier and its summary, matching the POC's pair format.
func RerankText(d Doc) string {
	s := d.ShortType() + "." + d.Name
	if d.Summary != "" {
		s += " — " + d.Summary
	}
	return s
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

// IsJunk reports whether d is masked from search results. Junk docs remain
// reachable through list_functions and describe_function.
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
	fields [numFields]*bm25Field
}

func newLexicalIndex(fields [][][]string) *lexicalIndex {
	ix := &lexicalIndex{}
	for f := 0; f < numFields; f++ {
		docs := fields[f]
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
func (ix *lexicalIndex) score(tokens []string, weights [numFields]float64) []float64 {
	n := ix.fields[0].n
	scores := make([]float64, n)
	seen := make(map[string]bool, len(tokens))
	for _, t := range tokens {
		if seen[t] {
			continue
		}
		seen[t] = true
		for f := 0; f < numFields; f++ {
			bf := ix.fields[f]
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
	vecs [numFields][]float32 // flat n*dim per field; zero rows for empty fields
	n    int
}

// embedBatch bounds one Embed call; static embedders are cheap per call and
// transformer ones want small batches, so this is a modest middle.
const embedBatch = 256

func buildDenseIndex(ctx context.Context, emb Embedder, docs []Doc) (*denseIndex, error) {
	d := &denseIndex{dim: emb.Dim(), n: len(docs)}
	for f := 0; f < numFields; f++ {
		d.vecs[f] = make([]float32, len(docs)*d.dim)
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
		for i, doc := range docs {
			t := fieldText(doc, f)
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
func (d *denseIndex) score(qv []float32, weights [numFields]float64) []float64 {
	scores := make([]float64, d.n)
	for f := 0; f < numFields; f++ {
		w := weights[f]
		vecs := d.vecs[f]
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

// topIdx returns the indices of the k highest-scoring eligible docs with a
// strictly positive score, best first.
func topIdx(scores []float64, mask []bool, k int) []int {
	var ids []int
	for i, s := range scores {
		if mask[i] && s > 0 {
			ids = append(ids, i)
		}
	}
	sort.SliceStable(ids, func(a, b int) bool { return scores[ids[a]] > scores[ids[b]] })
	if len(ids) > k {
		ids = ids[:k]
	}
	return ids
}

// rrf fuses ranked lists by reciprocal rank: score(doc) = Σ w_l / (k + rank_l).
func rrf(lists [][]int, weights []float64, k int) []int {
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
	sort.SliceStable(order, func(a, b int) bool { return score[order[a]] > score[order[b]] })
	return order
}
