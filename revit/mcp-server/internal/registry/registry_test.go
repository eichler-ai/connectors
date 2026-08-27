package registry

import (
	"testing"
	"time"
)

func TestRegisterAndGet(t *testing.T) {
	r := New()
	inst := &Instance{
		InstanceID:   "inst-1",
		PID:          1234,
		RevitVersion: "2027",
		Documents: []Document{
			{ID: "doc-abc", Title: "Sample.rvt", Active: true},
		},
	}
	r.Register(inst)

	got, ok := r.Get("inst-1")
	if !ok {
		t.Fatalf("Get: instance not found after Register")
	}
	if got.PID != 1234 || got.RevitVersion != "2027" {
		t.Errorf("Get returned wrong data: %+v", got)
	}
	if len(got.Documents) != 1 || got.Documents[0].ID != "doc-abc" {
		t.Errorf("Documents not preserved: %+v", got.Documents)
	}
}

func TestGetUnknownInstance(t *testing.T) {
	r := New()
	_, ok := r.Get("does-not-exist")
	if ok {
		t.Fatalf("Get should report not-found for an unregistered instance")
	}
}

func TestRegisterOverwritesExistingInstance(t *testing.T) {
	r := New()
	r.Register(&Instance{InstanceID: "inst-1", PID: 1, RevitVersion: "2027"})
	r.Register(&Instance{InstanceID: "inst-1", PID: 2, RevitVersion: "2027", Documents: []Document{{ID: "doc-x"}}})

	got, ok := r.Get("inst-1")
	if !ok {
		t.Fatalf("Get: not found")
	}
	if got.PID != 2 {
		t.Errorf("re-register should overwrite PID, got %d", got.PID)
	}
	if len(got.Documents) != 1 {
		t.Errorf("re-register should overwrite Documents, got %+v", got.Documents)
	}
}

func TestRemove(t *testing.T) {
	r := New()
	r.Register(&Instance{InstanceID: "inst-1"})
	r.Remove("inst-1")
	if _, ok := r.Get("inst-1"); ok {
		t.Fatalf("instance should be gone after Remove")
	}
	// Remove of an already-absent instance must not panic or error.
	r.Remove("inst-1")
}

func TestList(t *testing.T) {
	r := New()
	r.Register(&Instance{InstanceID: "inst-1"})
	r.Register(&Instance{InstanceID: "inst-2"})

	list := r.List()
	if len(list) != 2 {
		t.Fatalf("List() len = %d, want 2", len(list))
	}
	ids := map[string]bool{}
	for _, i := range list {
		ids[i.InstanceID] = true
	}
	if !ids["inst-1"] || !ids["inst-2"] {
		t.Errorf("List() missing expected instances: %+v", list)
	}
}

func TestRegisterDeepCopiesDocuments(t *testing.T) {
	r := New()
	docs := []Document{{ID: "doc-a"}}
	r.Register(&Instance{InstanceID: "inst-1", Documents: docs})

	docs[0].ID = "mutated"

	got, _ := r.Get("inst-1")
	if got.Documents[0].ID != "doc-a" {
		t.Errorf("registry should not alias caller's slice; got %+v", got.Documents)
	}
}

func TestWorkshareRoundTrips(t *testing.T) {
	r := New()
	r.Register(&Instance{InstanceID: "inst-1", Documents: []Document{{ID: "doc-a", Workshared: true}}})

	got, _ := r.Get("inst-1")
	if !got.Documents[0].Workshared {
		t.Errorf("Workshared should round-trip through Register/Get, got %+v", got.Documents[0])
	}
}

func TestRegisterStampsConnectedSinceWhenZero(t *testing.T) {
	r := New()
	r.Register(&Instance{InstanceID: "inst-1"})

	got, _ := r.Get("inst-1")
	if got.ConnectedSince.IsZero() {
		t.Errorf("ConnectedSince should be stamped on register")
	}
}

func TestIsResponsive_UnknownInstanceIsResponsive(t *testing.T) {
	r := New()
	if !r.IsResponsive("does-not-exist", time.Now()) {
		t.Errorf("an unknown instance_id should be reported responsive -- this method judges liveness, not registration")
	}
}

func TestIsResponsive_FallsBackToConnectedSinceBeforeFirstPing(t *testing.T) {
	r := New()
	r.Register(&Instance{InstanceID: "inst-1"})

	now := time.Now().Add(UnresponsiveThreshold - time.Second)
	if !r.IsResponsive("inst-1", now) {
		t.Errorf("instance should be responsive shortly after register, before its first ping, via the ConnectedSince fallback")
	}

	later := time.Now().Add(UnresponsiveThreshold + time.Second)
	if r.IsResponsive("inst-1", later) {
		t.Errorf("instance that never pinged should become unresponsive once ConnectedSince exceeds the threshold")
	}
}

func TestIsResponsive_TracksMostRecentPing(t *testing.T) {
	r := New()
	r.Register(&Instance{InstanceID: "inst-1"})
	r.RecordPing("inst-1")

	if !r.IsResponsive("inst-1", time.Now().Add(UnresponsiveThreshold-time.Second)) {
		t.Errorf("instance pinged recently should be responsive")
	}
	if r.IsResponsive("inst-1", time.Now().Add(UnresponsiveThreshold+time.Second)) {
		t.Errorf("instance with no ping for longer than the threshold should be unresponsive")
	}
}

func TestRecordPing_NoOpForUnregisteredInstance(t *testing.T) {
	r := New()
	r.RecordPing("does-not-exist") // must not panic
	if len(r.List()) != 0 {
		t.Errorf("a ping for an unregistered instance must not create a registry entry")
	}
}

func TestRegisterResetsLivenessOnReconnect(t *testing.T) {
	r := New()
	clock := time.Now()
	r.now = func() time.Time { return clock }

	r.Register(&Instance{InstanceID: "inst-1"})
	r.RecordPing("inst-1")

	// Advance the clock well past the old ping's staleness threshold, then
	// reconnect (re-register) — if reset didn't clear the old ping, a
	// query right after reconnect would incorrectly still see it as
	// "recently pinged" via the stale timestamp rather than via the fresh
	// ConnectedSince fallback.
	clock = clock.Add(UnresponsiveThreshold * 10)
	r.Register(&Instance{InstanceID: "inst-1"})

	if !r.IsResponsive("inst-1", clock) {
		t.Errorf("a fresh register should make the instance responsive again via ConnectedSince, not stay keyed to a stale pre-reconnect ping")
	}
}

func TestPruneStale_RemovesInstancesPastTheSilenceThreshold(t *testing.T) {
	r := New()
	clock := time.Now()
	r.now = func() time.Time { return clock }

	r.Register(&Instance{InstanceID: "stale"})

	// Advance the clock, then register+ping "fresh" — so its last-seen
	// timestamp is genuinely more recent than "stale"'s, not just
	// artificially compared against a shifted query time.
	clock = clock.Add(PruneAfterSilence / 2)
	r.Register(&Instance{InstanceID: "fresh"})
	r.RecordPing("fresh")

	clock = clock.Add(PruneAfterSilence/2 + time.Second)
	pruned := r.PruneStale(clock)

	if len(pruned) != 1 || pruned[0] != "stale" {
		t.Errorf("PruneStale should have pruned only 'stale', got %+v", pruned)
	}
	if _, ok := r.Get("stale"); ok {
		t.Errorf("'stale' should be gone from the registry after pruning")
	}
	if _, ok := r.Get("fresh"); !ok {
		t.Errorf("'fresh' (recently pinged) should not have been pruned")
	}
}

func TestPruneStale_NothingToPruneReturnsEmpty(t *testing.T) {
	r := New()
	r.Register(&Instance{InstanceID: "inst-1"})

	pruned := r.PruneStale(time.Now())
	if len(pruned) != 0 {
		t.Errorf("PruneStale should return nothing when no instance has gone silent, got %+v", pruned)
	}
}
