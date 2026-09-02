// Package howtosearch is the index behind search_howtos and describe_howto
// (revit/docs/howto-corpus-design.md §4-§5): the corpus embedded in the
// broker (howto.Embedded) overlaid with the user's local corpus, ranked by
// the same pipeline as search_functions (internal/semsearch) under a
// how-to field set, with the caller's Revit version as a post-ranking
// preference -- verified-here documents lead, nothing is filtered.
//
// The local directory is re-scanned on every call by a cheap signature
// (names, sizes, mtimes; no watcher), and the whole index is rebuilt when
// it changed: the corpus is tens to hundreds of documents, so a rebuild
// with the static embedder is milliseconds.
package howtosearch

import (
	"bytes"
	"context"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/howto"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/semsearch"
)

// Entry is one served document: the document, where it came from, what
// the sidecars say about it, and how the local overlay treated it.
type Entry struct {
	Doc      *howto.Document
	Source   string // howto.SourceSeed / SourceShared / SourceLocal
	Verified howto.Verification
	// Override carries the local-overlay outcome (SharedRev, and a Code +
	// Notice when the user should know something about their local copy).
	Override howto.Override
}

// VerifiedOn reports whether the document has a passing stamp for the
// version.
func (e Entry) VerifiedOn(revitVersion string) bool {
	st, ok := e.Verified.ByVersion[revitVersion]
	return ok && st.Status == howto.StampPassed
}

// Schema is the how-to field set. Weights are initial choices in the
// spirit of the API schema (design note §4), not measured ones: task is the
// search text by construction, title and the recorded hit phrasings are the
// next best, pitfalls and members catch an agent searching by symptom or by
// a member it already suspects. Tune once the queries corpus exists.
var Schema = semsearch.Schema[Entry]{
	Fields: []semsearch.Field[Entry]{
		{Name: "title", Text: func(e Entry) string { return e.Doc.Title }, Lexical: 1.0, Dense: 0.6},
		{Name: "task", Text: func(e Entry) string { return e.Doc.Task }, Lexical: 1.2, Dense: 1.0},
		{Name: "queries", Text: hitQueries, Lexical: 0.8, Dense: 0.6},
		{Name: "pitfalls", Text: pitfallText, Lexical: 0.5, Dense: 0.6},
		{Name: "members", Text: memberText, Lexical: 0.8, Dense: 0.2},
		{Name: "tags", Text: func(e Entry) string { return strings.Join(e.Doc.Tags, " ") }, Lexical: 0.5, Dense: 0.2},
	},
	// On an exact tie a reviewed document precedes an unreviewed local one.
	Before:     func(a, b Entry) bool { return a.Source != howto.SourceLocal && b.Source == howto.SourceLocal },
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
// that suspects "Wall.Create" scores on it without the namespace flooding
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
	Documents int // served (after overlay)
	Local     int // local documents loaded
	// LocalProblems, LocalSkipped and LocalTruncated report local files the
	// loader rejected or ignored, for notices[].
	LocalProblems  []string
	LocalSkipped   int
	LocalTruncated bool
	// NewerThanBroker is the highest document schema_version above the
	// broker's, or 0.
	NewerThanBroker int
	BuiltAt         time.Time
}

// Result is one ranked search.
type Result struct {
	Hits []semsearch.HitOf[Entry]
	// Dense and Reranked say which retrievers took part (see
	// manager.Result); Fingerprint identifies the ranked set for cursors.
	Dense, Reranked bool
	Fingerprint     string
	Status          Status
}

// maxCachedSearches bounds the ranked-list cache that makes cursor paging a
// slice (the cross-encoder is ~1s a call). Entries × ≤400 hits, tiny.
const maxCachedSearches = 16

type cachedSearch struct {
	key  string
	hits []semsearch.HitOf[Entry]
}

type state struct {
	ix          *semsearch.IndexOf[Entry]
	byID        map[string]Entry
	embedded    *howto.Corpus
	local       *howto.Corpus
	localSig    string
	fingerprint string
	dense       bool
	status      Status
}

// Service -- construct with New.
type Service struct {
	localDir string
	embedder semsearch.Embedder
	reranker semsearch.Reranker
	logf     func(string, ...any)

	mu       sync.Mutex
	st       *state
	searches []cachedSearch
}

// New builds a Service over the embedded corpus and localDir. embedder and
// reranker may be nil (lexical-only / no rerank), exactly as for the API
// index. Nothing is loaded until the first call.
func New(localDir string, embedder semsearch.Embedder, reranker semsearch.Reranker, logf func(string, ...any)) *Service {
	if logf == nil {
		logf = func(string, ...any) {}
	}
	return &Service{localDir: localDir, embedder: embedder, reranker: reranker, logf: logf}
}

// Embedded returns the corpus compiled into the broker (nil when it failed
// to load), for callers that need it as a lookup base.
func (s *Service) Embedded() *howto.Corpus {
	c, _, _, _ := howto.Embedded()
	return c
}

// Search ranks the corpus for query, preferring documents verified on
// revitVersion within the head of the list.
func (s *Service) Search(ctx context.Context, query, revitVersion string) (Result, error) {
	st, err := s.current(ctx)
	if err != nil {
		return Result{}, err
	}
	res := Result{Dense: st.dense, Reranked: s.reranker != nil, Fingerprint: st.fingerprint, Status: st.status}
	key := st.fingerprint + "\x00" + revitVersion + "\x00" + strings.ToLower(strings.TrimSpace(query))
	if hits, ok := s.cachedSearch(key); ok {
		res.Hits = hits
		return res, nil
	}
	q := semsearch.QueryOf[Entry]{Text: query, Reranker: s.reranker,
		Prefer: func(e Entry) bool { return e.VerifiedOn(revitVersion) }}
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

// Describe returns the served document for id, following an absorbs
// pointer (redirectedFrom is then the id asked for).
func (s *Service) Describe(ctx context.Context, id string) (e Entry, redirectedFrom string, status Status, ok bool, err error) {
	st, err := s.current(ctx)
	if err != nil {
		return Entry{}, "", Status{}, false, err
	}
	if e, ok := st.byID[id]; ok {
		return e, "", st.status, true, nil
	}
	for _, c := range []*howto.Corpus{st.local, st.embedded} {
		if c == nil {
			continue
		}
		if d, to, found := c.Get(id); found && to != "" && d != nil {
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

// current returns the index for the local directory as it is now,
// rebuilding when the directory's signature changed.
func (s *Service) current(ctx context.Context) (*state, error) {
	sig := localSignature(s.localDir)
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.st != nil && s.st.localSig == sig {
		return s.st, nil
	}
	st, err := s.build(ctx, sig)
	if err != nil {
		return nil, err
	}
	s.st = st
	s.searches = nil
	return st, nil
}

// localSignature is a cheap fingerprint of the local corpus directory:
// every .json file and the sidecar with size and mtime. Empty when the
// directory does not exist.
func localSignature(dir string) string {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return ""
	}
	var b strings.Builder
	for _, e := range entries {
		if e.IsDir() || (filepath.Ext(e.Name()) != ".json" && e.Name() != howto.SessionSidecarName) {
			continue
		}
		info, err := e.Info()
		if err != nil {
			continue
		}
		fmt.Fprintf(&b, "%s|%d|%d\n", e.Name(), info.Size(), info.ModTime().UnixNano())
	}
	return b.String()
}

func (s *Service) build(ctx context.Context, sig string) (*state, error) {
	start := time.Now()
	embedded, stamps, ver, err := howto.Embedded()
	if err != nil {
		return nil, fmt.Errorf("embedded how-to corpus: %w", err)
	}
	local, err := howto.LoadLocalDir(s.localDir)
	if err != nil {
		return nil, err
	}
	st := &state{byID: map[string]Entry{}, embedded: embedded, local: local, localSig: sig}
	st.status = Status{Version: ver, Local: local.Len(), LocalProblems: local.Problems, LocalSkipped: local.Skipped,
		LocalTruncated: local.Truncated, NewerThanBroker: local.NewerThanBroker, BuiltAt: start}
	if embedded.NewerThanBroker > st.status.NewerThanBroker {
		st.status.NewerThanBroker = embedded.NewerThanBroker
	}
	stamps = append(append([]howto.Stamp(nil), stamps...), s.localStamps(local)...)

	ids := map[string]bool{}
	for _, id := range embedded.IDs() {
		ids[id] = true
	}
	for _, id := range local.IDs() {
		ids[id] = true
	}
	sorted := make([]string, 0, len(ids))
	for id := range ids {
		sorted = append(sorted, id)
	}
	sort.Strings(sorted)
	entries := make([]Entry, 0, len(sorted))
	for _, id := range sorted {
		o, ok := howto.Overlay(embedded, local, id)
		if !ok {
			continue
		}
		e := Entry{Doc: o.Doc, Source: o.Source, Verified: howto.VerifiedOn(o.Doc, stamps), Override: o}
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
	st.fingerprint = ver.Hash + ":" + shortHash(sig)
	s.logf("howtosearch: index ready in %v (%d documents, %d local, dense=%v)", time.Since(start).Round(time.Millisecond), len(entries), local.Len(), st.dense)
	return st, nil
}

// localStamps reads the session sidecar beside the local documents; a
// missing or unreadable one is simply no stamps.
func (s *Service) localStamps(local *howto.Corpus) []howto.Stamp {
	raw, err := os.ReadFile(filepath.Join(s.localDir, howto.SessionSidecarName))
	if err != nil || len(bytes.TrimSpace(raw)) == 0 {
		return nil
	}
	sc, err := howto.LoadSidecar(bytes.NewReader(raw))
	if err != nil {
		s.logf("howtosearch: local sidecar: %v", err)
		return nil
	}
	kept, _ := sc.Prune(local)
	return kept
}

func shortHash(s string) string {
	if s == "" {
		return "0"
	}
	h := uint32(2166136261)
	for i := 0; i < len(s); i++ {
		h ^= uint32(s[i])
		h *= 16777619
	}
	return fmt.Sprintf("%08x", h)
}
