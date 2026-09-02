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
	"sync"
	"time"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/semsearch"
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
	// pageSize members per dump_members call: ~300 bytes each, ~1.5MB a
	// page, far under the wire's 64MiB line cap; the 2027 corpus is ~16 pages.
	pageSize = 5000
	// maxMembers caps one corpus; the 2027 core API is 76,601 members and a
	// heavy add-in load roughly doubles that.
	maxMembers = 400_000
	// buildTimeout bounds the whole page-and-index run.
	buildTimeout = 5 * time.Minute
	// maxCachedIndexes bounds the fingerprint cache: each index is ~250MB
	// for the full corpus with dense vectors, so keep only a few.
	maxCachedIndexes = 3
)

// Status is a snapshot of one instance's index.
type Status struct {
	State        State
	Fingerprint  string
	Members      int
	Dense        bool // dense retriever attached (models available and embedded)
	Err          error
	BuiltAt      time.Time
	RevitVersion string
}

type entry struct {
	fingerprint  string
	revitVersion string
	ix           *semsearch.Index
	members      int
	dense        bool
	builtAt      time.Time
	err          error
	done         chan struct{} // closed when the build finished (ready or failed)
}

// Manager -- construct with New.
type Manager struct {
	src      Source
	embedder semsearch.Embedder
	reranker semsearch.Reranker
	logf     func(string, ...any)

	mu         sync.Mutex
	byInstance map[string]*entry
	byPrint    map[string]*built // ready indexes by fingerprint, oldest evicted first
	printOrder []string
}

// New builds a Manager. embedder and reranker may be nil (lexical-only).
func New(src Source, embedder semsearch.Embedder, reranker semsearch.Reranker, logf func(string, ...any)) *Manager {
	if logf == nil {
		logf = func(string, ...any) {}
	}
	return &Manager{src: src, embedder: embedder, reranker: reranker, logf: logf,
		byInstance: map[string]*entry{}, byPrint: map[string]*built{}}
}

// HasModels reports whether the dense retriever and reranker are wired.
func (m *Manager) HasModels() bool { return m.embedder != nil && m.reranker != nil }

// OnAttach starts (or reuses) the index build for instanceID. Idempotent:
// the add-in re-registers on every document event, so repeated calls while a
// build is running or an index is ready are no-ops.
func (m *Manager) OnAttach(instanceID, revitVersion string) {
	m.mu.Lock()
	if e, ok := m.byInstance[instanceID]; ok {
		select {
		case <-e.done:
			if e.err == nil {
				m.mu.Unlock()
				return // ready
			}
			// failed earlier: allow a rebuild on the next attach
		default:
			m.mu.Unlock()
			return // building
		}
	}
	e := &entry{revitVersion: revitVersion, done: make(chan struct{})}
	m.byInstance[instanceID] = e
	m.mu.Unlock()
	go m.build(instanceID, e)
}

// OnDetach forgets the instance. Its index stays cached by fingerprint for a
// reconnect or a sibling instance.
func (m *Manager) OnDetach(instanceID string) {
	m.mu.Lock()
	delete(m.byInstance, instanceID)
	m.mu.Unlock()
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
		return Status{State: StateBuilding, RevitVersion: e.revitVersion}
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	if e.err != nil {
		return Status{State: StateFailed, Err: e.err, RevitVersion: e.revitVersion}
	}
	return Status{State: StateReady, Fingerprint: e.fingerprint, Members: e.members, Dense: e.dense, BuiltAt: e.builtAt, RevitVersion: e.revitVersion}
}

// WaitReady blocks until the instance's build finishes or ctx ends; for tests
// and for callers that prefer a short wait over a degraded answer.
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

// Search runs the full pipeline (dense when models are wired, cross-encoder
// rerank over the default pool) for instanceID. Namespace is an exact-match
// pre-mask; an empty string means unscoped.
func (m *Manager) Search(ctx context.Context, instanceID, query, namespace string) ([]semsearch.Hit, error) {
	m.mu.Lock()
	e, ok := m.byInstance[instanceID]
	m.mu.Unlock()
	if !ok {
		return nil, ErrNotReady
	}
	select {
	case <-e.done:
	default:
		return nil, ErrNotReady
	}
	m.mu.Lock()
	ix, dense, buildErr := e.ix, e.dense, e.err
	m.mu.Unlock()
	if buildErr != nil {
		return nil, ErrNotReady
	}
	q := semsearch.Query{Text: query, Namespace: namespace}
	if dense {
		q.Embedder = m.embedder
	}
	if m.reranker != nil {
		q.Reranker = m.reranker
		q.RerankPool = semsearch.DefaultRerankPool
	}
	return ix.Search(ctx, q)
}

// --- build -------------------------------------------------------------------

type dumpPage struct {
	Members []struct {
		MemberID      string `json:"member_id"`
		Kind          string `json:"kind"`
		Namespace     string `json:"namespace"`
		DeclaringType string `json:"declaring_type"`
		Name          string `json:"name"`
		Signature     string `json:"signature"`
		Summary       string `json:"summary"`
		Core          bool   `json:"core"`
	} `json:"members"`
	Total       int    `json:"total"`
	NextOffset  *int   `json:"next_offset"`
	Fingerprint string `json:"fingerprint"`
}

// built is the outcome of one build, published into the entry under m.mu.
type built struct {
	fingerprint string
	ix          *semsearch.Index
	members     int
	dense       bool
	builtAt     time.Time
}

func (m *Manager) build(instanceID string, e *entry) {
	ctx, cancel := context.WithTimeout(context.Background(), buildTimeout)
	defer cancel()
	start := time.Now()
	res, err := m.buildCorpus(ctx, instanceID)
	m.mu.Lock()
	if err != nil {
		e.err = err
	} else {
		e.fingerprint, e.ix, e.members, e.dense, e.builtAt = res.fingerprint, res.ix, res.members, res.dense, res.builtAt
	}
	m.mu.Unlock()
	if err != nil {
		m.logf("semsearch: index build for instance %s failed after %v: %v", instanceID, time.Since(start).Round(time.Millisecond), err)
	} else {
		m.logf("semsearch: index for instance %s ready in %v (%d members, fingerprint %.12s, dense=%v)",
			instanceID, time.Since(start).Round(time.Millisecond), res.members, res.fingerprint, res.dense)
	}
	close(e.done)
}

func (m *Manager) buildCorpus(ctx context.Context, instanceID string) (built, error) {
	var res built
	var docs []semsearch.Doc
	offset := 0
	for {
		raw, _, drec := m.src.DumpMembers(ctx, instanceID, offset, pageSize)
		if drec != nil {
			return res, fmt.Errorf("dump_members at offset %d: %s: %s", offset, drec.Code, drec.Message)
		}
		var page dumpPage
		if err := json.Unmarshal(raw, &page); err != nil {
			return res, fmt.Errorf("dump_members at offset %d: decoding: %w", offset, err)
		}
		if offset == 0 {
			// Reuse a cached index for this exact corpus, if we have one.
			if cached := m.lookupPrint(page.Fingerprint); cached != nil {
				m.logf("semsearch: instance %s reuses the cached index for fingerprint %.12s", instanceID, page.Fingerprint)
				return *cached, nil
			}
			res.fingerprint = page.Fingerprint
			if page.Total > maxMembers {
				return res, fmt.Errorf("corpus reports %d members, above the %d bound", page.Total, maxMembers)
			}
			docs = make([]semsearch.Doc, 0, page.Total)
		} else if page.Fingerprint != res.fingerprint {
			return res, fmt.Errorf("corpus changed mid-dump (fingerprint %.12s -> %.12s); the add-in re-synced", res.fingerprint, page.Fingerprint)
		}
		for _, r := range page.Members {
			docs = append(docs, semsearch.Doc{MemberID: r.MemberID, Kind: r.Kind, Namespace: r.Namespace, DeclaringType: r.DeclaringType,
				Name: r.Name, Signature: r.Signature, Summary: r.Summary, Core: r.Core})
		}
		if len(docs) > maxMembers {
			return res, fmt.Errorf("corpus exceeded the %d member bound", maxMembers)
		}
		if page.NextOffset == nil || len(page.Members) == 0 {
			break
		}
		if *page.NextOffset <= offset {
			return res, fmt.Errorf("dump_members next_offset %d does not advance past %d", *page.NextOffset, offset)
		}
		offset = *page.NextOffset
	}

	ix := semsearch.Build(docs)
	if m.embedder != nil {
		if err := ix.Embed(ctx, m.embedder); err != nil {
			return res, fmt.Errorf("embedding corpus: %w", err)
		}
		res.dense = true
	}
	res.ix, res.members, res.builtAt = ix, len(docs), time.Now()
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
