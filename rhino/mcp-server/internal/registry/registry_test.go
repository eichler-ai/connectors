package registry

import (
	"testing"
	"time"
)

func inst(id string) *Instance {
	return &Instance{InstanceID: id, PID: 1, RhinoVersion: "8", Platform: "macos", Documents: []Document{{ID: "tmp-1", Title: "Untitled", Active: true}}}
}

func TestRegisterMintsEpochAndSameEpochReplaceKeepsConnectedSince(t *testing.T) {
	r := New()
	t0 := time.Date(2026, 9, 10, 0, 0, 0, 0, time.UTC)
	e1 := r.Register(inst("a"), 0, t0)
	if e1 == 0 {
		t.Fatal("epoch must be minted")
	}
	r.RecordPing("a", e1, t0.Add(time.Second), &MemorySample{ManagedMB: 5})
	// A document event re-registers over the same connection.
	updated := inst("a")
	updated.Documents = append(updated.Documents, Document{ID: "doc-2", Title: "Tower"})
	e2 := r.Register(updated, e1, t0.Add(time.Minute))
	got, _ := r.Get("a")
	if e2 != e1 || !got.ConnectedSince.Equal(t0) || got.Memory == nil || len(got.Documents) != 2 {
		t.Fatalf("same-epoch replace: epoch %d->%d, since %v, mem %v, docs %d", e1, e2, got.ConnectedSince, got.Memory, len(got.Documents))
	}
	// A fresh connection (epoch 0) mints a new epoch and resets connected-since.
	e3 := r.Register(inst("a"), 0, t0.Add(2*time.Minute))
	got, _ = r.Get("a")
	if e3 == e1 || !got.ConnectedSince.Equal(t0.Add(2*time.Minute)) {
		t.Fatalf("fresh register: epoch %d, since %v", e3, got.ConnectedSince)
	}
}

func TestStaleEpochRegisterIsRefused(t *testing.T) {
	// A displaced connection re-registering (a document event on a half-open socket) must not
	// take ownership of the live entry (review of #281).
	r := New()
	now := time.Now()
	old := r.Register(inst("a"), 0, now)
	live := r.Register(inst("a"), 0, now.Add(time.Second))
	stale := inst("a")
	stale.Documents = nil
	if got := r.Register(stale, old, now.Add(2*time.Second)); got != 0 {
		t.Fatalf("stale epoch register returned %d, want 0", got)
	}
	cur, _ := r.Get("a")
	if len(cur.Documents) != 1 || r.epochs["a"] != live {
		t.Fatalf("live entry was disturbed: %+v epoch %d", cur, r.epochs["a"])
	}
	if r.Register(inst("zzz"), 42, now) != 0 {
		t.Fatal("an epoch for an unknown instance is refused too")
	}
}

func TestRecordPingForUnknownInstanceDoesNotPanic(t *testing.T) {
	r := New()
	r.RecordPing("ghost", 1, time.Now(), &MemorySample{})
}

func TestRemoveIfEpochGuardsAgainstStaleTeardown(t *testing.T) {
	r := New()
	now := time.Now()
	old := r.Register(inst("a"), 0, now)
	live := r.Register(inst("a"), 0, now.Add(time.Second)) // redial displaced the first
	if r.RemoveIfEpoch("a", old) {
		t.Fatal("the old connection's teardown must not remove the live replacement")
	}
	if _, ok := r.Get("a"); !ok {
		t.Fatal("entry gone")
	}
	if !r.RemoveIfEpoch("a", live) {
		t.Fatal("the live epoch removes")
	}
}

func TestPingWithStaleEpochIsIgnored(t *testing.T) {
	r := New()
	now := time.Now()
	old := r.Register(inst("a"), 0, now)
	live := r.Register(inst("a"), 0, now)
	r.RecordPing("a", old, now.Add(time.Hour), nil)
	if r.IsResponsive("a", now.Add(time.Hour+time.Second)) {
		t.Fatal("a stale connection's ping must not refresh liveness")
	}
	r.RecordPing("a", live, now.Add(time.Hour), nil)
	if !r.IsResponsive("a", now.Add(time.Hour+time.Second)) {
		t.Fatal("live ping counts")
	}
}

func TestPruneStaleReturnsEpochsAndListOrders(t *testing.T) {
	r := New()
	now := time.Now()
	ea := r.Register(inst("a"), 0, now)
	r.Register(inst("b"), 0, now.Add(time.Second))
	r.RecordPing("b", r.epochs["b"], now.Add(PruneAfterSilence), nil)
	pruned := r.PruneStale(now.Add(PruneAfterSilence))
	if len(pruned) != 1 || pruned["a"] != ea {
		t.Fatalf("pruned = %v", pruned)
	}
	list := r.List()
	if len(list) != 1 || list[0].InstanceID != "b" {
		t.Fatalf("list = %+v", list)
	}
	// Copies, not aliases.
	list[0].Documents[0].Title = "mutated"
	got, _ := r.Get("b")
	if got.Documents[0].Title == "mutated" {
		t.Fatal("List must return copies")
	}
}
