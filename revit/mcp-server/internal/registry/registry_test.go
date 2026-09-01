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
	r.Register(inst, time.Now())

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
	r.Register(&Instance{InstanceID: "inst-1", PID: 1, RevitVersion: "2027"}, time.Now())
	r.Register(&Instance{InstanceID: "inst-1", PID: 2, RevitVersion: "2027", Documents: []Document{{ID: "doc-x"}}}, time.Now())

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

// (An unconditional Remove used to live here; it lost its last production
// caller when connection teardown moved to the epoch-guarded RemoveIfEpoch
// and the prune sweep kept its own in-place deletion, so it was deleted
// rather than kept as dead API. RemoveIfEpoch's own test covers the
// absent-id no-op case.)

func TestList(t *testing.T) {
	r := New()
	r.Register(&Instance{InstanceID: "inst-1"}, time.Now())
	r.Register(&Instance{InstanceID: "inst-2"}, time.Now())

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
	r.Register(&Instance{InstanceID: "inst-1", Documents: docs}, time.Now())

	docs[0].ID = "mutated"

	got, _ := r.Get("inst-1")
	if got.Documents[0].ID != "doc-a" {
		t.Errorf("registry should not alias caller's slice; got %+v", got.Documents)
	}
}

func TestWorksharedRoundTrips(t *testing.T) {
	r := New()
	r.Register(&Instance{InstanceID: "inst-1", Documents: []Document{{ID: "doc-a", Workshared: true}}}, time.Now())

	got, _ := r.Get("inst-1")
	if !got.Documents[0].Workshared {
		t.Errorf("Workshared should round-trip through Register/Get, got %+v", got.Documents[0])
	}
}

func TestRegisterStampsConnectedSinceWhenZero(t *testing.T) {
	r := New()
	r.Register(&Instance{InstanceID: "inst-1"}, time.Now())

	got, _ := r.Get("inst-1")
	if got.ConnectedSince.IsZero() {
		t.Errorf("ConnectedSince should be stamped on register")
	}
}

func TestIsResponsiveForUnknownInstance(t *testing.T) {
	r := New()
	if !r.IsResponsive("does-not-exist", time.Now()) {
		t.Errorf("an unknown instance_id should be reported responsive -- this method judges liveness, not registration")
	}
}

func TestIsResponsiveFallsBackToConnectedSinceBeforeFirstPing(t *testing.T) {
	r := New()
	r.Register(&Instance{InstanceID: "inst-1"}, time.Now())

	now := time.Now().Add(UnresponsiveThreshold - time.Second)
	if !r.IsResponsive("inst-1", now) {
		t.Errorf("instance should be responsive shortly after register, before its first ping, via the ConnectedSince fallback")
	}

	later := time.Now().Add(UnresponsiveThreshold + time.Second)
	if r.IsResponsive("inst-1", later) {
		t.Errorf("instance that never pinged should become unresponsive once ConnectedSince exceeds the threshold")
	}
}

func TestIsResponsiveTracksMostRecentPing(t *testing.T) {
	r := New()
	r.Register(&Instance{InstanceID: "inst-1"}, time.Now())
	r.RecordPing("inst-1", time.Now(), nil)

	if !r.IsResponsive("inst-1", time.Now().Add(UnresponsiveThreshold-time.Second)) {
		t.Errorf("instance pinged recently should be responsive")
	}
	if r.IsResponsive("inst-1", time.Now().Add(UnresponsiveThreshold+time.Second)) {
		t.Errorf("instance with no ping for longer than the threshold should be unresponsive")
	}
}

func TestRecordPingNoOpForUnregisteredInstance(t *testing.T) {
	r := New()
	r.RecordPing("does-not-exist", time.Now(), nil) // must not panic
	if len(r.List()) != 0 {
		t.Errorf("a ping for an unregistered instance must not create a registry entry")
	}
}

func TestRegisterResetsLivenessOnReconnect(t *testing.T) {
	r := New()
	clock := time.Now()

	r.Register(&Instance{InstanceID: "inst-1"}, clock)
	r.RecordPing("inst-1", clock, nil)

	// Advance the clock well past the old ping's staleness threshold, then
	// reconnect (re-register) — if reset didn't clear the old ping, a
	// query right after reconnect would incorrectly still see it as
	// "recently pinged" via the stale timestamp rather than via the fresh
	// ConnectedSince fallback.
	clock = clock.Add(UnresponsiveThreshold * 10)
	r.Register(&Instance{InstanceID: "inst-1"}, clock)

	if !r.IsResponsive("inst-1", clock) {
		t.Errorf("a fresh register should make the instance responsive again via ConnectedSince, not stay keyed to a stale pre-reconnect ping")
	}
}

func TestPruneStaleRemovesInstancesPastTheSilenceThreshold(t *testing.T) {
	r := New()
	clock := time.Now()

	r.Register(&Instance{InstanceID: "stale"}, clock)

	// Advance the clock, then register+ping "fresh" — so its last-seen
	// timestamp is genuinely more recent than "stale"'s, not just
	// artificially compared against a shifted query time.
	clock = clock.Add(PruneAfterSilence / 2)
	r.Register(&Instance{InstanceID: "fresh"}, clock)
	r.RecordPing("fresh", clock, nil)

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

func TestPruneStaleNothingToPruneReturnsEmpty(t *testing.T) {
	r := New()
	r.Register(&Instance{InstanceID: "inst-1"}, time.Now())

	pruned := r.PruneStale(time.Now())
	if len(pruned) != 0 {
		t.Errorf("PruneStale should return nothing when no instance has gone silent, got %+v", pruned)
	}
}

// TestRemoveIfEpochIgnoresStaleEpoch pins the epoch guard on the
// connection-teardown removal path (v1 integrated review): a stale
// connection's teardown, holding the epoch its own register minted, must
// not remove a later registration under the same instance_id — including
// the interleaving where the new connection's Register has already run
// before the old connection's teardown gets to its registry removal.
func TestRemoveIfEpochIgnoresStaleEpoch(t *testing.T) {
	r := New()
	now := time.Now()

	epochA := r.Register(&Instance{InstanceID: "inst-1", PID: 1}, now)
	epochB := r.Register(&Instance{InstanceID: "inst-1", PID: 2}, now) // the redial's re-register
	if epochA == epochB {
		t.Fatalf("re-register must mint a fresh epoch, got %d twice", epochA)
	}

	r.RemoveIfEpoch("inst-1", epochA) // the stale connection's late teardown
	inst, ok := r.Get("inst-1")
	if !ok {
		t.Fatal("stale-epoch removal deleted the live registration")
	}
	if inst.PID != 2 {
		t.Errorf("PID = %d, want the re-registered entry (2)", inst.PID)
	}

	r.RemoveIfEpoch("inst-1", epochB) // the current connection's teardown
	if _, ok := r.Get("inst-1"); ok {
		t.Fatal("current-epoch removal should have applied")
	}

	// Removing an already-removed (or never-registered) id is a no-op.
	r.RemoveIfEpoch("inst-1", epochB)
	r.RemoveIfEpoch("never-registered", 42)
}

// TestMemorySamplePersistsAcrossReRegisterAndSurfacesInList covers issue #31's
// heartbeat-memory path: a ping's sample surfaces in List(), a bare ping leaves
// it nil, a doc-event re-register (which REPLACES the Instance) does NOT wipe it,
// and removal cleans it up.
func TestMemorySamplePersistsAcrossReRegisterAndSurfacesInList(t *testing.T) {
	find := func(list []*Instance, id string) *Instance {
		for _, inst := range list {
			if inst.InstanceID == id {
				return inst
			}
		}
		return nil
	}

	r := New()
	epoch := r.Register(&Instance{InstanceID: "inst-1"}, time.Now())

	// A bare ping (no sample) records liveness but leaves Memory nil.
	r.RecordPing("inst-1", time.Now(), nil)
	if got := find(r.List(), "inst-1"); got == nil || got.Memory != nil {
		t.Fatalf("bare ping: want registered instance with nil Memory, got %+v", got)
	}

	// A ping carrying a sample surfaces it in List().
	r.RecordPing("inst-1", time.Now(), &MemorySample{PrivateMB: 1234, WorkingSetMB: 567, ManagedMB: 89})
	if got := find(r.List(), "inst-1"); got == nil || got.Memory == nil || got.Memory.PrivateMB != 1234 {
		t.Fatalf("sample not surfaced in List: %+v", got)
	}

	// KEY PROPERTY: a doc-event re-register replaces the Instance outright but must
	// NOT wipe the memory sample -- it lives in a separate map keyed by instance_id.
	epoch = r.Register(&Instance{InstanceID: "inst-1", RevitVersion: "2025"}, time.Now())
	if got := find(r.List(), "inst-1"); got == nil || got.Memory == nil || got.Memory.PrivateMB != 1234 {
		t.Fatalf("re-register wiped the memory sample: %+v", got)
	}

	// A ping for an unregistered instance stores nothing (no leak).
	r.RecordPing("ghost", time.Now(), &MemorySample{PrivateMB: 9})
	if find(r.List(), "ghost") != nil {
		t.Fatalf("ping for an unregistered instance created an entry")
	}

	// Removal (with the CURRENT epoch) cleans up the sample; re-registering the
	// same id then starts fresh with nil Memory.
	r.RemoveIfEpoch("inst-1", epoch)
	r.Register(&Instance{InstanceID: "inst-1"}, time.Now())
	if got := find(r.List(), "inst-1"); got == nil || got.Memory != nil {
		t.Fatalf("memory sample survived removal: %+v", got)
	}
}
