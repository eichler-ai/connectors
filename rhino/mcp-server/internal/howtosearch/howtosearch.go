// Package howtosearch is the index behind search_howtos and describe_howto:
// the corpus embedded in the broker (howto.Embedded), ranked by the same
// pipeline as search_functions (internal/servercore/semsearch) under a how-to
// field set, with the caller's Rhino version as a post-ranking preference --
// verified-here documents lead, nothing is filtered.
//
// This is the Rhino analog of revit/mcp-server/internal/howtosearch, read-side
// only: there is no local corpus overlay in v1, so the index is built once
// from the immutable embedded corpus (the embedder runs on the first call).
package howtosearch

import (
	"context"
	"fmt"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/eichler-ai/connectors/internal/servercore/semsearch"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/howto"
)

// Entry is one served document: the document, where it came from, and what the
// sidecar says about it.
type Entry struct {
	Doc      *howto.Document
	Source   string // howto.SourceSeed
	Verified howto.Verification
}

// VerifiedOn reports whether the document has a passing stamp for the version.
func (e Entry) VerifiedOn(rhinoVersion string) bool {
	st, ok := e.Verified.ByVersion[rhinoVersion]
	return ok && st.Status == howto.StampPassed
}

// Schema is the how-to field set. Weights mirror the Revit how-to index: task
// is the search text by construction, title and the recorded hit phrasings are
// the next best, pitfalls and members catch an agent searching by symptom or
// by a member it already suspects.
var Schema = semsearch.Schema[Entry]{
	Fields: []semsearch.Field[Entry]{
		{Name: "title", Text: func(e Entry) string { return e.Doc.Title }, Lexical: 1.0, Dense: 0.6},
		{Name: "task", Text: func(e Entry) string { return e.Doc.Task }, Lexical: 1.2, Dense: 1.0},
		{Name: "queries", Text: hitQueries, Lexical: 0.8, Dense: 0.6},
		{Name: "pitfalls", Text: pitfallText, Lexical: 0.5, Dense: 0.6},
		{Name: "members", Text: memberText, Lexical: 0.8, Dense: 0.2},
		{Name: "tags", Text: func(e Entry) string { return strings.Join(e.Doc.Tags, " ") }, Lexical: 0.5, Dense: 0.2},
	},
	RerankText: func(e Entry) string { return e.Doc.Title + " — " + e.Doc.Task },
}

func hitQueries(e Entry) string {
	if e.Doc.Queries == nil {
		return ""
	}
	var parts []string
	for _, q := range e.Doc.Queries.Hit {
		parts = append(parts, q.Text)
	}
	return strings.Join(parts, ". ")
}

func pitfallText(e Entry) string {
	var parts []string
	for _, p := range e.Doc.Pitfalls {
		parts = append(parts, p.Symptom, p.Cause, p.Fix)
	}
	return strings.Join(parts, " ")
}

// memberText is the members as Type.Member (namespace dropped), so an agent
// that suspects "Sphere.#ctor" scores on it without the namespace flooding
// every document with the same tokens.
func memberText(e Entry) string {
	var parts []string
	for _, m := range e.Doc.Members {
		segs := strings.Split(m, ".")
		if len(segs) >= 2 {
			m = segs[len(segs)-2] + "." + segs[len(segs)-1]
		}
		parts = append(parts, m)
	}
	return strings.Join(parts, " ")
}

// Status describes the corpus a response was served from.
type Status struct {
	Version   howto.Version
	Documents int
	// NewerThanBroker is the highest document schema_version above the
	// broker's, or 0.
	NewerThanBroker int
	BuiltAt         time.Time
}

// Result is one ranked search.
type Result struct {
	Hits []semsearch.HitOf[Entry]
	// Dense and Reranked say which retrievers took part; Fingerprint identifies
	// the ranked set for cursors.
	Dense, Reranked bool
	Fingerprint     string
	Status          Status
}

// maxCachedSearches bounds the ranked-list cache that makes cursor paging a
// slice (the cross-encoder is ~1s a call).
const maxCachedSearches = 16

type cachedSearch struct {
	key  string
	hits []semsearch.HitOf[Entry]
}

type state struct {
	ix          *semsearch.IndexOf[Entry]
	byID        map[string]Entry
	embedded    *howto.Corpus
	fingerprint string
	dense       bool
	status      Status
}

// LoadError is a failure to load or index the embedded corpus, as opposed to a
// failure while ranking one query.
type LoadError struct{ Err error }

func (e *LoadError) Error() string { return e.Err.Error() }
func (e *LoadError) Unwrap() error { return e.Err }

// Service -- construct with New.
type Service struct {
	embedder semsearch.Embedder
	reranker semsearch.Reranker
	logf     func(string, ...any)

	mu       sync.Mutex
	st       *state
	buildErr error
	searches []cachedSearch
}

// New builds a Service over the embedded corpus. embedder and reranker may be
// nil (lexical-only / no rerank), exactly as for the API index. Nothing is
// loaded until the first call.
func New(embedder semsearch.Embedder, reranker semsearch.Reranker, logf func(string, ...any)) *Service {
	if logf == nil {
		logf = func(string, ...any) {}
	}
	return &Service{embedder: embedder, reranker: reranker, logf: logf}
}

// Embedded returns the corpus compiled into the broker (nil when it failed to
// load), for callers that need it as a lookup base.
func (s *Service) Embedded() *howto.Corpus {
	c, _, _, _ := howto.Embedded()
	return c
}

// Search ranks the corpus for query, preferring documents verified on
// rhinoVersion within the head of the list.
func (s *Service) Search(ctx context.Context, query, rhinoVersion string) (Result, error) {
	st, err := s.current(ctx)
	if err != nil {
		return Result{}, err
	}
	res := Result{Dense: st.dense, Reranked: s.reranker != nil, Fingerprint: st.fingerprint, Status: st.status}
	key := st.fingerprint + "\x00" + rhinoVersion + "\x00" + strings.ToLower(strings.TrimSpace(query))
	if hits, ok := s.cachedSearch(key); ok {
		res.Hits = hits
		return res, nil
	}
	q := semsearch.QueryOf[Entry]{Text: query, Reranker: s.reranker,
		Prefer: func(e Entry) bool { return e.VerifiedOn(rhinoVersion) }}
	if st.dense {
		q.Embedder = s.embedder
	}
	hits, err := st.ix.Search(ctx, q)
	if err != nil {
		return Result{}, err
	}
	s.rememberSearch(key, hits)
	res.Hits = hits
	return res, nil
}

// Describe returns the served document for id, following an absorbs pointer
// (redirectedFrom is then the id asked for).
func (s *Service) Describe(ctx context.Context, id string) (e Entry, redirectedFrom string, status Status, ok bool, err error) {
	st, err := s.current(ctx)
	if err != nil {
		return Entry{}, "", Status{}, false, err
	}
	if e, ok := st.byID[id]; ok {
		return e, "", st.status, true, nil
	}
	if st.embedded != nil {
		if d, to, found := st.embedded.Get(id); found && to != "" && d != nil {
			if e, ok := st.byID[to]; ok {
				return e, id, st.status, true, nil
			}
		}
	}
	return Entry{}, "", st.status, false, nil
}

// Status reports the corpus as currently loaded (loading it if needed).
func (s *Service) Status(ctx context.Context) (Status, error) {
	st, err := s.current(ctx)
	if err != nil {
		return Status{}, err
	}
	return st.status, nil
}

func (s *Service) cachedSearch(key string) ([]semsearch.HitOf[Entry], bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, c := range s.searches {
		if c.key == key {
			return c.hits, true
		}
	}
	return nil, false
}

func (s *Service) rememberSearch(key string, hits []semsearch.HitOf[Entry]) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.searches = append(s.searches, cachedSearch{key: key, hits: hits})
	if len(s.searches) > maxCachedSearches {
		s.searches = s.searches[1:]
	}
}

// current returns the index, building it once from the immutable embedded
// corpus. A build failure is remembered so a later call does not retry a
// hopeless load on every request.
func (s *Service) current(ctx context.Context) (*state, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.st != nil {
		return s.st, nil
	}
	if s.buildErr != nil {
		return nil, &LoadError{Err: s.buildErr}
	}
	st, err := s.build(ctx)
	if err != nil {
		s.buildErr = err
		return nil, &LoadError{Err: err}
	}
	s.st = st
	return st, nil
}

func (s *Service) build(ctx context.Context) (*state, error) {
	start := time.Now()
	embedded, stamps, ver, err := howto.Embedded()
	if err != nil {
		return nil, fmt.Errorf("embedded how-to corpus: %w", err)
	}
	st := &state{byID: map[string]Entry{}, embedded: embedded}
	st.status = Status{Version: ver, NewerThanBroker: embedded.NewerThanBroker, BuiltAt: start}

	ids := embedded.IDs()
	sort.Strings(ids)
	entries := make([]Entry, 0, len(ids))
	for _, id := range ids {
		d, _, ok := embedded.Get(id)
		if !ok {
			continue
		}
		e := Entry{Doc: d, Source: embedded.Source, Verified: howto.VerifiedOn(d, stamps)}
		st.byID[id] = e
		entries = append(entries, e)
	}
	st.status.Documents = len(entries)
	st.ix = semsearch.BuildWith(Schema, entries)
	if s.embedder != nil {
		if err := st.ix.Embed(ctx, s.embedder); err != nil {
			return nil, fmt.Errorf("embedding how-to corpus: %w", err)
		}
		st.dense = true
	}
	st.fingerprint = ver.Hash
	s.logf("howtosearch: index ready in %v (%d documents, dense=%v)", time.Since(start).Round(time.Millisecond), len(entries), st.dense)
	return st, nil
}
