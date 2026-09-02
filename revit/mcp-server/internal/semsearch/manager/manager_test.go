package manager

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/semsearch"
)

// fakeSource serves a scripted corpus over the dump_members shape, per
// instance, and counts calls so tests can prove reuse and paging.
type fakeSource struct {
	mu          sync.Mutex
	corpora     map[string][]semsearch.Doc // instance -> docs
	fingerprint map[string]string
	calls       atomic.Int32
	failAt      int // offset at which to return a wire error; -1 never
	pageCap     int // server-side page cap; 0 = honour limit
}

func newFakeSource() *fakeSource {
	return &fakeSource{corpora: map[string][]semsearch.Doc{}, fingerprint: map[string]string{}, failAt: -1}
}

func (f *fakeSource) set(instance, fp string, docs []semsearch.Doc) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.corpora[instance], f.fingerprint[instance] = docs, fp
}

func (f *fakeSource) DumpMembers(_ context.Context, instanceID string, offset, limit int) (json.RawMessage, string, *diag.Record) {
	f.calls.Add(1)
	f.mu.Lock()
	docs, fp := f.corpora[instanceID], f.fingerprint[instanceID]
	f.mu.Unlock()
	if docs == nil {
		return nil, "", diag.New(diag.SeverityError, "instance-not-found", "test", "no such instance "+instanceID)
	}
	if f.failAt >= 0 && offset >= f.failAt {
		return nil, "", diag.New(diag.SeverityError, "wire-call-failed", "test", "boom")
	}
	if f.pageCap > 0 && limit > f.pageCap {
		limit = f.pageCap
	}
	end := offset + limit
	if end > len(docs) {
		end = len(docs)
	}
	if offset > len(docs) {
		offset = len(docs)
	}
	page := map[string]any{"members": toWire(docs[offset:end]), "total": len(docs), "fingerprint": fp}
	if end < len(docs) {
		page["next_offset"] = end
	}
	raw, _ := json.Marshal(page)
	return raw, "2027", nil
}

func toWire(docs []semsearch.Doc) []map[string]any {
	out := make([]map[string]any, len(docs))
	for i, d := range docs {
		out[i] = map[string]any{"member_id": d.MemberID, "kind": d.Kind, "namespace": d.Namespace, "declaring_type": d.DeclaringType,
			"name": d.Name, "signature": d.Signature, "summary": d.Summary, "core": d.Core}
	}
	return out
}

func corpus(n int) []semsearch.Doc {
	docs := make([]semsearch.Doc, n)
	for i := range docs {
		docs[i] = semsearch.Doc{MemberID: fmt.Sprintf("M:Autodesk.Revit.DB.Type%d.Member%d", i, i), Kind: "Method", Namespace: "Autodesk.Revit.DB",
			DeclaringType: fmt.Sprintf("Autodesk.Revit.DB.Type%d", i), Name: fmt.Sprintf("Member%d", i), Summary: fmt.Sprintf("Does thing number %d.", i), Core: true}
	}
	docs[0] = semsearch.Doc{MemberID: "M:Autodesk.Revit.DB.Wall.Create", Kind: "Method", Namespace: "Autodesk.Revit.DB", DeclaringType: "Autodesk.Revit.DB.Wall",
		Name: "Create", Summary: "Creates a new rectangular profile wall within the project.", Core: true}
	return docs
}

func waitReady(t *testing.T, m *Manager, inst string) Status {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	st := m.WaitReady(ctx, inst)
	if st.State == StateBuilding {
		t.Fatalf("index for %s still building after 5s", inst)
	}
	return st
}

func TestBuildPagesWholeCorpusAndServesSearch(t *testing.T) {
	src := newFakeSource()
	src.pageCap = 7 // force many pages regardless of the manager's own page size
	src.set("inst-1", "fp-a", corpus(30))
	m := New(src, nil, nil, t.Logf)

	if st := m.Status("inst-1"); st.State != StateUnknown {
		t.Fatalf("state before attach = %s", st.State)
	}
	if _, err := m.Search(context.Background(), "inst-1", "wall", ""); !errors.Is(err, ErrNotReady) {
		t.Fatalf("Search before attach err = %v, want ErrNotReady", err)
	}

	m.OnAttach("inst-1")
	st := waitReady(t, m, "inst-1")
	if st.State != StateReady || st.Members != 30 || st.Fingerprint != "fp-a" || st.Dense {
		t.Fatalf("status = %+v", st)
	}
	if got := src.calls.Load(); got < 5 {
		t.Fatalf("expected paging through the capped source, got %d calls", got)
	}
	res, err := m.Search(context.Background(), "inst-1", "create wall", "")
	if err != nil || len(res.Hits) == 0 || res.Hits[0].Doc.MemberID != "M:Autodesk.Revit.DB.Wall.Create" || res.Dense || res.Fingerprint != "fp-a" {
		t.Fatalf("res=%+v err=%v", res, err)
	}
	// The identical search is served from the ranked-list cache: same hits,
	// no second pipeline run (the reranker counts calls in
	// TestModelsAreWiredIntoSearch; here we check identity of the slice).
	again, _ := m.Search(context.Background(), "inst-1", "  Create Wall ", "")
	if len(again.Hits) != len(res.Hits) || &again.Hits[0] != &res.Hits[0] {
		t.Fatalf("repeat search did not hit the cache")
	}
}

func TestAttachIsIdempotentWhileBuildingAndWhenReady(t *testing.T) {
	src := newFakeSource()
	src.set("inst-1", "fp-a", corpus(10))
	m := New(src, nil, nil, nil)
	m.OnAttach("inst-1")
	m.OnAttach("inst-1") // doc-event re-register during the build
	waitReady(t, m, "inst-1")
	calls := src.calls.Load()
	m.OnAttach("inst-1") // and once ready
	time.Sleep(50 * time.Millisecond)
	if src.calls.Load() != calls {
		t.Fatalf("re-attach triggered another dump: %d -> %d calls", calls, src.calls.Load())
	}
}

func TestSameFingerprintReusesIndexAcrossInstances(t *testing.T) {
	src := newFakeSource()
	docs := corpus(20)
	src.set("inst-1", "fp-shared", docs)
	src.set("inst-2", "fp-shared", docs)
	m := New(src, nil, nil, nil)
	m.OnAttach("inst-1")
	waitReady(t, m, "inst-1")
	before := src.calls.Load()

	m.OnAttach("inst-2")
	st := waitReady(t, m, "inst-2")
	if st.State != StateReady || st.Members != 20 {
		t.Fatalf("inst-2 status = %+v", st)
	}
	// One call (the first page, to learn the fingerprint) and no more.
	if got := src.calls.Load() - before; got != 1 {
		t.Fatalf("second instance made %d dump calls, want 1", got)
	}

	// Detach + reattach of the same instance also reuses.
	m.OnDetach("inst-1")
	if st := m.Status("inst-1"); st.State != StateUnknown {
		t.Fatalf("after detach state = %s", st.State)
	}
	before = src.calls.Load()
	m.OnAttach("inst-1")
	waitReady(t, m, "inst-1")
	if got := src.calls.Load() - before; got != 1 {
		t.Fatalf("reattach made %d dump calls, want 1", got)
	}
}

func TestWireFailureMarksFailedAndAllowsRebuildOnNextAttach(t *testing.T) {
	src := newFakeSource()
	src.pageCap = 5
	src.set("inst-1", "fp-a", corpus(12))
	src.failAt = 5
	m := New(src, nil, nil, nil)
	m.OnAttach("inst-1")
	st := waitReady(t, m, "inst-1")
	if st.State != StateFailed || st.Err == nil || st.Err.Code != "search-index-build-failed" || st.Err.Detail["cause"] == nil {
		t.Fatalf("status = %+v, want failed with a §01 record carrying the wire cause", st)
	}
	if _, err := m.Search(context.Background(), "inst-1", "wall", ""); !errors.Is(err, ErrNotReady) {
		t.Fatalf("Search on failed index err = %v", err)
	}
	src.failAt = -1
	m.OnAttach("inst-1")
	if st := waitReady(t, m, "inst-1"); st.State != StateReady {
		t.Fatalf("rebuild status = %+v", st)
	}
}

func TestFingerprintChangeMidDumpFails(t *testing.T) {
	src := &flippingSource{fakeSource: newFakeSource()}
	src.pageCap = 5
	src.set("inst-1", "fp-a", corpus(12))
	m := New(src, nil, nil, nil)
	m.OnAttach("inst-1")
	st := waitReady(t, m, "inst-1")
	if st.State != StateFailed {
		t.Fatalf("status = %+v, want failed on fingerprint flip", st)
	}
}

// flippingSource changes its fingerprint after the first page, simulating an
// add-in re-sync mid-dump.
type flippingSource struct{ *fakeSource }

func (f *flippingSource) DumpMembers(ctx context.Context, instanceID string, offset, limit int) (json.RawMessage, string, *diag.Record) {
	if offset > 0 {
		f.set(instanceID, "fp-b", f.corpora[instanceID])
	}
	return f.fakeSource.DumpMembers(ctx, instanceID, offset, limit)
}

// constEmbedder is a trivial Embedder so the dense path is exercised end to end.
type constEmbedder struct{}

func (constEmbedder) Dim() int { return 2 }
func (constEmbedder) Embed(_ context.Context, texts []string) ([][]float32, error) {
	out := make([][]float32, len(texts))
	for i := range texts {
		out[i] = []float32{1, 0}
	}
	return out, nil
}

type countingReranker struct{ calls atomic.Int32 }

func (r *countingReranker) Score(_ context.Context, _ string, docs []string) ([]float32, error) {
	r.calls.Add(1)
	return make([]float32, len(docs)), nil
}

func TestModelsAreWiredIntoSearch(t *testing.T) {
	src := newFakeSource()
	src.set("inst-1", "fp-a", corpus(10))
	rr := &countingReranker{}
	m := New(src, constEmbedder{}, rr, nil)
	m.OnAttach("inst-1")
	st := waitReady(t, m, "inst-1")
	if !st.Dense {
		t.Fatalf("status = %+v, want dense", st)
	}
	res, err := m.Search(context.Background(), "inst-1", "create wall", "")
	if err != nil || !res.Dense {
		t.Fatalf("res=%+v err=%v", res, err)
	}
	if rr.calls.Load() != 1 {
		t.Fatalf("reranker calls = %d, want 1", rr.calls.Load())
	}
	// A cursor page re-asks the same query: served from cache, no rerank.
	if _, err := m.Search(context.Background(), "inst-1", "create wall", ""); err != nil {
		t.Fatal(err)
	}
	if rr.calls.Load() != 1 {
		t.Fatalf("repeat search re-ran the reranker: %d calls", rr.calls.Load())
	}
	// A different namespace scope is a different ranked set.
	if _, err := m.Search(context.Background(), "inst-1", "create wall", "Autodesk.Revit.DB"); err != nil {
		t.Fatal(err)
	}
	if rr.calls.Load() != 2 {
		t.Fatalf("scoped search should rerank once more: %d calls", rr.calls.Load())
	}
}
