// Package manager owns one search_functions index per connected Revit
// instance: it pages the member corpus from the add-in (dump_members) when an
// instance attaches, builds the semsearch.Index (lexical, then dense), and
// serves searches against it. Indexes are shared by corpus fingerprint, so two
// instances of the same Revit build with the same add-ins get one index and a
// reconnect costs nothing.
//
// The manager never blocks a tool call on a build: Search reports the index
// state and the tool layer decides how to degrade (today: forward to the
// add-in's own keyword ranker until the index is ready).
package manager

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"sync"
	"time"

	"github.com/eichler-ai/connectors/internal/servercore/diag"
	"github.com/eichler-ai/connectors/internal/servercore/semsearch"
)

// Source is the wire side the manager pages the corpus from; *discovery.Router
// satisfies it.
type Source interface {
	DumpMembers(ctx context.Context, instanceID string, offset, limit int) (json.RawMessage, string, *diag.Record)
}

// State of an instance's index.
type State int

const (
	// StateUnknown: no build has been started for this instance (it was
	// never attached, or was detached).
	StateUnknown State = iota
	StateBuilding
	StateReady
	StateFailed
)

func (s State) String() string {
	switch s {
	case StateBuilding:
		return "building"
	case StateReady:
		return "ready"
	case StateFailed:
		return "failed"
	}
	return "unknown"
}

// Bounds (CONVENTIONS.md: every retained buffer states its bound).
const (
	// maxCachedSearches bounds the ranked-list cache that makes cursor paging
	// a slice instead of a re-run of the whole pipeline (the cross-encoder
	// alone is ~1s): entries × up to 2*candidateDepth hits, well under 1MB.
	maxCachedSearches = 16
	// pageSize members per dump_members call: ~300 bytes each, ~1.5MB a
	// page, far under the wire's 64MiB line cap; the 2027 corpus is ~16 pages.
	pageSize = 5000
	// maxMembers caps one corpus; the 2027 core API is 76,601 members and a
	// heavy add-in load roughly doubles that.
	maxMembers = 400_000
	// buildTimeout bounds the whole page-and-index run.
	buildTimeout = 5 * time.Minute
	// maxBuildAttempts bounds how many times a failed build is retried on
	// re-register (the add-in re-registers on every document event); after
	// that the instance stays failed until it disconnects and reconnects.
	maxBuildAttempts = 3
	// maxCachedIndexes bounds the fingerprint cache: each index is ~235MB for
	// the 2027 corpus with dense vectors (76k × 3 fields × 256 × 4B), so this
	// is up to ~700MB resident. Keep only a few.
	maxCachedIndexes = 3
)

// Status is a snapshot of one instance's index.
type Status struct {
	State       State
	Fingerprint string
	Members     int
	Dense       bool // dense retriever attached (models available and embedded)
	// Err is the build failure as a §01 record (code search-index-build-failed);
	// nil unless State is StateFailed.
	Err     *diag.Record
	BuiltAt time.Time
}

// built is the outcome of one build; entry embeds it. Every field is written
// before done is closed, and only read after, so the close is the fence.
type built struct {
	fingerprint string
	ix          *semsearch.Index
	members     int
	dense       bool
	builtAt     time.Time
}

type entry struct {
	built
	err      *diag.Record
	attempts int           // builds started for this instance, including this one
	done     chan struct{} // closed when the build finished (ready or failed)
}

// cachedSearch is one ranked list, kept so cursor pages are slices.
type cachedSearch struct {
	key  string
	hits []semsearch.Hit
}

// Manager -- construct with New.
type Manager struct {
	src      Source
	embedder semsearch.Embedder
	reranker semsearch.Reranker
	logf     func(string, ...any)

	mu sync.Mutex
	// byInstance is bounded by the connected instances (the broker detaches
	// on disconnect and the registry prunes dead ones).
	byInstance map[string]*entry
	byPrint    map[string]*built // ready indexes by fingerprint, oldest evicted first
	printOrder []string
	searches   []cachedSearch // most recent last, bounded by maxCachedSearches
	// rebuilding marks instances with a background rebuild in flight (fingerprint
	// changed), so RefreshIfChanged never starts a second one; the old index keeps
	// serving until the rebuild swaps it in.
	rebuilding map[string]struct{}
}

// New builds a Manager. embedder and reranker may be nil (lexical-only).
func New(src Source, embedder semsearch.Embedder, reranker semsearch.Reranker, logf func(string, ...any)) *Manager {
	if logf == nil {
		logf = func(string, ...any) {}
	}
	return &Manager{src: src, embedder: embedder, reranker: reranker, logf: logf,
		byInstance: map[string]*entry{}, byPrint: map[string]*built{}, rebuilding: map[string]struct{}{}}
}

// OnAttach starts (or reuses) the index build for instanceID. Idempotent:
// the add-in re-registers on every document event, so repeated calls while a
// build is running or an index is ready are no-ops.
func (m *Manager) OnAttach(instanceID string) {
	m.mu.Lock()
	attempts := 0
	if e, ok := m.byInstance[instanceID]; ok {
		select {
		case <-e.done:
			if e.err == nil || e.attempts >= maxBuildAttempts {
				m.mu.Unlock()
				return // ready, or failed for good until the instance reconnects
			}
			attempts = e.attempts // failed earlier: retry, bounded
		default:
			m.mu.Unlock()
			return // building
		}
	}
	e := &entry{done: make(chan struct{}), attempts: attempts + 1}
	m.byInstance[instanceID] = e
	m.mu.Unlock()
	go m.build(instanceID, e)
}

// OnDetach forgets the instance. Its index stays cached by fingerprint for a
// reconnect or a sibling instance.
func (m *Manager) OnDetach(instanceID string) {
	m.mu.Lock()
	delete(m.byInstance, instanceID)
	delete(m.rebuilding, instanceID) // an in-flight rebuild won't swap now (identity check); clear for tidiness
	m.mu.Unlock()
}

// RefreshAll cheaply re-checks every connected instance's corpus fingerprint and
// rebuilds any whose corpus changed since its index was built (e.g. Grasshopper
// was opened, or a plug-in loaded, after the index was first built). Meant to be
// called periodically from the server's existing poll loop. Each check is one
// small dump_members(0,1) round trip; a rebuild runs in the background while the
// current index keeps serving.
func (m *Manager) RefreshAll(ctx context.Context) {
	m.mu.Lock()
	ids := make([]string, 0, len(m.byInstance))
	for id := range m.byInstance {
		ids = append(ids, id)
	}
	m.mu.Unlock()
	for _, id := range ids {
		m.RefreshIfChanged(ctx, id)
	}
}

// RefreshIfChanged polls instanceID's current corpus fingerprint and, if it
// differs from the ready index's, starts a background rebuild. A no-op unless
// the instance has a READY index, no rebuild is already in flight, and the
// fingerprint actually changed — so it never disturbs a first build, a failed
// build, or an unchanged corpus.
func (m *Manager) RefreshIfChanged(ctx context.Context, instanceID string) {
	m.mu.Lock()
	e, ok := m.byInstance[instanceID]
	_, busy := m.rebuilding[instanceID]
	current := ""
	if ok && isReady(e) {
		current = e.fingerprint
	}
	if current == "" || busy {
		m.mu.Unlock()
		return // no ready index to refresh, or a rebuild is already running
	}
	m.mu.Unlock()

	fp, ok := m.pollFingerprint(ctx, instanceID)
	if !ok || fp == "" || fp == current {
		return // poll failed, or the corpus is unchanged
	}

	m.mu.Lock()
	// Re-check under the lock: the instance may have detached, rebuilt, or a
	// rebuild may have started since the poll.
	e, ok = m.byInstance[instanceID]
	if _, busy = m.rebuilding[instanceID]; !ok || !isReady(e) || e.fingerprint == fp || busy {
		m.mu.Unlock()
		return
	}
	m.rebuilding[instanceID] = struct{}{}
	m.mu.Unlock()
	go m.rebuild(instanceID, e)
}

// rebuild pages and builds a fresh index for an instance whose corpus changed,
// then swaps it in — keeping the old index serving until the swap, and dropping
// the result if the instance detached meanwhile. Reuses a cached index when the
// new fingerprint is one we already hold (buildCorpus checks byPrint).
func (m *Manager) rebuild(instanceID string, prev *entry) {
	// Own build context (background + buildTimeout), like build(): a rebuild that started near shutdown may
	// run up to buildTimeout after the server ctx cancels, but the process is exiting anyway.
	ctx, cancel := context.WithTimeout(context.Background(), buildTimeout)
	defer cancel()
	start := time.Now()
	res, drec := m.buildCorpus(ctx, instanceID)
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.rebuilding, instanceID)
	if drec != nil {
		m.logf("semsearch: rebuild for instance %s failed after %v: %s (keeping the previous index)", instanceID, time.Since(start).Round(time.Millisecond), drec.Message)
		return
	}
	// Swap only if the entry we set out to replace is still the current one: a detach (map entry gone) or a
	// newer build/rebuild (different entry, e.g. a reconnect that built a fresh corpus) must NOT be clobbered
	// by this now-possibly-stale result. The fresh index stays cached by fingerprint regardless.
	if cur, still := m.byInstance[instanceID]; !still || cur != prev {
		return
	}
	done := make(chan struct{})
	close(done)
	m.byInstance[instanceID] = &entry{built: res, done: done, attempts: 1}
	m.logf("semsearch: rebuilt index for instance %s in %v (%d members, fingerprint %.12s, dense=%v)",
		instanceID, time.Since(start).Round(time.Millisecond), res.members, res.fingerprint, res.dense)
}

// pollFingerprint fetches just the current corpus fingerprint (one member is
// enough; the add-in computes the fingerprint independently of the page).
func (m *Manager) pollFingerprint(ctx context.Context, instanceID string) (string, bool) {
	raw, _, drec := m.src.DumpMembers(ctx, instanceID, 0, 1)
	if drec != nil {
		return "", false
	}
	var page dumpPage
	if err := json.Unmarshal(raw, &page); err != nil {
		return "", false
	}
	return page.Fingerprint, true
}

// isReady reports whether a build has finished successfully. Caller holds m.mu.
func isReady(e *entry) bool {
	select {
	case <-e.done:
		return e.err == nil
	default:
		return false
	}
}

// Status reports the instance's index state.
func (m *Manager) Status(instanceID string) Status {
	m.mu.Lock()
	e, ok := m.byInstance[instanceID]
	m.mu.Unlock()
	if !ok {
		return Status{State: StateUnknown}
	}
	select {
	case <-e.done:
	default:
		return Status{State: StateBuilding}
	}
	if e.err != nil {
		return Status{State: StateFailed, Err: e.err}
	}
	return Status{State: StateReady, Fingerprint: e.fingerprint, Members: e.members, Dense: e.dense, BuiltAt: e.builtAt}
}

// WaitReady blocks until the instance's build finishes or ctx ends. Test
// helper; production never waits on a build (it falls back instead).
func (m *Manager) WaitReady(ctx context.Context, instanceID string) Status {
	m.mu.Lock()
	e, ok := m.byInstance[instanceID]
	m.mu.Unlock()
	if !ok {
		return Status{State: StateUnknown}
	}
	select {
	case <-e.done:
	case <-ctx.Done():
	}
	return m.Status(instanceID)
}

// ErrNotReady is returned by Search while the index is building, failed, or
// unknown; Status carries the detail.
var ErrNotReady = errors.New("search index not ready")

// Result is one ranked search over a ready index.
type Result struct {
	Hits []semsearch.Hit
	// Dense reports whether the dense retriever took part; false means
	// lexical-only. Reranked reports whether the cross-encoder re-scored the
	// pool -- false when no reranker is wired (e.g. its model failed to load).
	Dense    bool
	Reranked bool
	// Fingerprint identifies the corpus the ranking came from, so a paging
	// cursor can be tied to exactly this ranked set.
	Fingerprint string
}

// Search runs the full pipeline (dense when models are wired, cross-encoder
// rerank over the default pool) for instanceID. Namespace is an exact-match
// pre-mask; an empty string means unscoped. Repeated identical searches on
// the same corpus are served from a small cache, which is what makes cursor
// paging cheap.
func (m *Manager) Search(ctx context.Context, instanceID, query, namespace string) (Result, error) {
	m.mu.Lock()
	e, ok := m.byInstance[instanceID]
	m.mu.Unlock()
	if !ok {
		return Result{}, ErrNotReady
	}
	select {
	case <-e.done:
	default:
		return Result{}, ErrNotReady
	}
	if e.err != nil {
		return Result{}, ErrNotReady
	}
	res := Result{Dense: e.dense, Reranked: m.reranker != nil, Fingerprint: e.fingerprint}
	key := e.fingerprint + "\x00" + namespace + "\x00" + strings.ToLower(strings.TrimSpace(query))
	if hits, ok := m.cachedSearch(key); ok {
		res.Hits = hits
		return res, nil
	}
	q := semsearch.Query{Text: query, Mask: semsearch.InNamespace(namespace), Reranker: m.reranker}
	if e.dense {
		q.Embedder = m.embedder
	}
	hits, err := e.ix.Search(ctx, q)
	if err != nil {
		return Result{}, err
	}
	m.rememberSearch(key, hits)
	res.Hits = hits
	return res, nil
}

func (m *Manager) cachedSearch(key string) ([]semsearch.Hit, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()
	for _, c := range m.searches {
		if c.key == key {
			return c.hits, true
		}
	}
	return nil, false
}

func (m *Manager) rememberSearch(key string, hits []semsearch.Hit) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.searches = append(m.searches, cachedSearch{key: key, hits: hits})
	if len(m.searches) > maxCachedSearches {
		m.searches = m.searches[1:]
	}
}

// --- build -------------------------------------------------------------------

type dumpPage struct {
	Members     []semsearch.Doc `json:"members"`
	Total       int             `json:"total"`
	NextOffset  *int            `json:"next_offset"`
	Fingerprint string          `json:"fingerprint"`
}

const source = "mcp-server.internal.semsearch.manager"

// buildFailed wraps a build failure as the §01 record search_functions
// reports in its notices; cause may be a wire diag.Record (kept in detail).
func buildFailed(instanceID string, cause *diag.Record, err error) *diag.Record {
	msg := "the search_functions index for instance " + instanceID + " could not be built"
	if err != nil {
		msg += ": " + err.Error()
	}
	rec := diag.New(diag.SeverityWarning, "search-index-build-failed", source, msg).
		WithRemedy("search_functions keeps answering from the add-in's keyword ranker; the build is retried on the instance's next register event (up to 3 attempts), then when it reconnects")
	detail := map[string]any{"instance_id": instanceID}
	if cause != nil {
		detail["cause"] = cause
	}
	return rec.WithDetail(detail)
}

func (m *Manager) build(instanceID string, e *entry) {
	ctx, cancel := context.WithTimeout(context.Background(), buildTimeout)
	defer cancel()
	start := time.Now()
	res, err := m.buildCorpus(ctx, instanceID)
	if err != nil {
		e.err = err
		cause := ""
		if c, ok := err.Detail["cause"].(*diag.Record); ok && c != nil {
			cause = " (cause: " + c.Code + ": " + c.Message + ")"
		}
		m.logf("semsearch: index build for instance %s failed after %v: %s%s", instanceID, time.Since(start).Round(time.Millisecond), err.Message, cause)
	} else {
		e.built = res
		m.logf("semsearch: index for instance %s ready in %v (%d members, fingerprint %.12s, dense=%v)",
			instanceID, time.Since(start).Round(time.Millisecond), res.members, res.fingerprint, res.dense)
	}
	close(e.done)
}

func (m *Manager) buildCorpus(ctx context.Context, instanceID string) (built, *diag.Record) {
	var res built
	var docs []semsearch.Doc
	offset := 0
	fail := func(err error) (built, *diag.Record) { return res, buildFailed(instanceID, nil, err) }
	for {
		raw, _, drec := m.src.DumpMembers(ctx, instanceID, offset, pageSize)
		if drec != nil {
			return res, buildFailed(instanceID, drec, fmt.Errorf("dump_members at offset %d failed", offset))
		}
		var page dumpPage
		if err := json.Unmarshal(raw, &page); err != nil {
			return fail(fmt.Errorf("dump_members at offset %d: decoding: %w", offset, err))
		}
		if offset == 0 {
			// Reuse a cached index for this exact corpus, if we have one.
			if cached := m.lookupPrint(page.Fingerprint); cached != nil {
				m.logf("semsearch: instance %s reuses the cached index for fingerprint %.12s", instanceID, page.Fingerprint)
				return *cached, nil
			}
			res.fingerprint = page.Fingerprint
			if page.Total > maxMembers {
				return fail(fmt.Errorf("corpus reports %d members, above the %d bound", page.Total, maxMembers))
			}
			docs = make([]semsearch.Doc, 0, page.Total)
		} else if page.Fingerprint != res.fingerprint {
			return fail(fmt.Errorf("corpus changed mid-dump (fingerprint %.12s -> %.12s); the add-in re-synced", res.fingerprint, page.Fingerprint))
		}
		docs = append(docs, page.Members...)
		if len(docs) > maxMembers {
			return fail(fmt.Errorf("corpus exceeded the %d member bound", maxMembers))
		}
		if page.NextOffset == nil || len(page.Members) == 0 {
			break
		}
		if *page.NextOffset <= offset {
			return fail(fmt.Errorf("dump_members next_offset %d does not advance past %d", *page.NextOffset, offset))
		}
		offset = *page.NextOffset
	}

	ix := semsearch.Build(docs)
	if m.embedder != nil {
		if err := ix.Embed(ctx, m.embedder); err != nil {
			return fail(fmt.Errorf("embedding corpus: %w", err))
		}
		res.dense = true
	}
	res.ix, res.members, res.builtAt = ix, ix.Len(), time.Now()
	m.storePrint(res)
	return res, nil
}

func (m *Manager) lookupPrint(fp string) *built {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.byPrint[fp]
}

func (m *Manager) storePrint(b built) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if _, ok := m.byPrint[b.fingerprint]; !ok {
		m.printOrder = append(m.printOrder, b.fingerprint)
	}
	m.byPrint[b.fingerprint] = &b
	for len(m.printOrder) > maxCachedIndexes {
		old := m.printOrder[0]
		m.printOrder = m.printOrder[1:]
		delete(m.byPrint, old)
	}
}
