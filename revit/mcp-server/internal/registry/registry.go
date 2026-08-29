// Package registry implements the broker's in-memory instance registry —
// the "register" notification handling described in PRD §05, plus the
// heartbeat-derived liveness tracking (PRD §05 "Heartbeat, not just
// connection state") that backs list_instances' status field.
package registry

import (
	"sync"
	"time"
)

// UnresponsiveThreshold is how long without a heartbeat ping (PRD §05)
// before an instance is considered unresponsive rather than merely quiet.
// Assumes the add-in's own ping interval (BridgeHost.cs, PingIntervalMs =
// 10s) — roughly 3 missed pings. The two aren't automatically coupled
// across languages; changing one should prompt reconsidering the other.
const UnresponsiveThreshold = 30 * time.Second

// PruneAfterSilence is how long an instance can go without a heartbeat
// ping before it's dropped from the registry entirely (PRD §05). A cleanly
// disconnected instance is already removed immediately by the broker's own
// connection-teardown path (RemoveIfEpoch) — this sweep exists for the case PRD
// §05 actually describes: a socket that's still open but has gone quiet
// (Revit wedged without the connection dropping), which Remove alone can
// never catch.
const PruneAfterSilence = 5 * time.Minute

// Document mirrors one entry of an instance's `register` document list
// (PRD §05/§09) — the full list_instances shape.
type Document struct {
	ID         string `json:"document_id"`
	Title      string `json:"title"`
	Path       string `json:"path"`
	Workshared bool   `json:"workshared"`
	Active     bool   `json:"active"`
}

// Instance is the broker's live record of one connected Revit MCP Bridge,
// populated from its `register` notification (PRD §05).
type Instance struct {
	InstanceID     string     `json:"instance_id"`
	PID            int        `json:"pid"`
	RevitVersion   string     `json:"revit_version"`
	Documents      []Document `json:"documents"`
	ConnectedSince time.Time  `json:"connected_since"`
}

// Registry is the broker's thread-safe, in-memory instance table, keyed by
// instance_id.
type Registry struct {
	mu         sync.RWMutex
	instances  map[string]*Instance
	lastPingAt map[string]time.Time

	// epochs tracks a monotonically-increasing registration epoch per
	// instance_id, minted by Register and consumed by RemoveIfEpoch — see
	// that method for the reconnect-overlap race this exists to close.
	epochs    map[string]uint64
	nextEpoch uint64
}

// New creates an empty Registry.
func New() *Registry {
	return &Registry{
		instances:  make(map[string]*Instance),
		lastPingAt: make(map[string]time.Time),
		epochs:     make(map[string]uint64),
	}
}

// cloneInstance returns a copy of inst with its own backing Documents
// slice, so callers can't mutate the registry's internal state through a
// returned pointer.
func cloneInstance(inst *Instance) *Instance {
	cp := *inst
	cp.Documents = append([]Document(nil), inst.Documents...)
	return &cp
}

// Register records or replaces the entry for inst.InstanceID and returns
// the registration's epoch — a token the registering connection's own
// teardown later hands to RemoveIfEpoch, so a stale connection can never
// remove a newer registration (see RemoveIfEpoch). A second register for
// an already-known instance_id (e.g. after a reconnect, per PRD §05)
// overwrites the prior entry outright rather than merging it. now is the
// caller-supplied clock reading used to stamp ConnectedSince when inst
// doesn't already specify one (same caller-supplies-now convention as
// IsResponsive/PruneStale/RecordPing below — Registry itself schedules
// nothing and so has no need for an injected clock field).
func (r *Registry) Register(inst *Instance, now time.Time) uint64 {
	cp := cloneInstance(inst)
	if cp.ConnectedSince.IsZero() {
		cp.ConnectedSince = now.UTC()
	}

	r.mu.Lock()
	defer r.mu.Unlock()
	r.instances[cp.InstanceID] = cp
	r.nextEpoch++
	r.epochs[cp.InstanceID] = r.nextEpoch
	// A fresh register (first connect or reconnect) supersedes whatever
	// silence preceded it — reset liveness tracking so a just-reconnected
	// instance isn't immediately eligible for pruning based on a ping
	// timestamp from before the reconnect.
	delete(r.lastPingAt, cp.InstanceID)
	return r.nextEpoch
}

// Get returns the current record for instanceID, if any.
func (r *Registry) Get(instanceID string) (*Instance, bool) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	inst, ok := r.instances[instanceID]
	if !ok {
		return nil, false
	}
	return cloneInstance(inst), true
}

// RemoveIfEpoch drops instanceID only if epoch is still its current
// registration epoch. This is the connection-teardown form of Remove,
// closing a reconnect-overlap race (v1 integrated review): a half-open
// connection's serve goroutine can observe its socket error long after the
// add-in has redialed and re-registered the same stable instance_id, and
// an unconditional Remove at that point would delete the live replacement's
// entry. Each register mints a fresh epoch, so a teardown holding the
// epoch its own connection's register minted can never remove a later
// registration — including in the narrow interleaving where the new
// connection's Register has run but its execution-manager attach hasn't
// yet, which is why this is keyed on the registry's own epoch rather than
// on the execution manager's conn-identity answer.
func (r *Registry) RemoveIfEpoch(instanceID string, epoch uint64) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.epochs[instanceID] != epoch {
		return
	}
	delete(r.instances, instanceID)
	delete(r.lastPingAt, instanceID)
	delete(r.epochs, instanceID)
}

// List returns a snapshot of every currently-registered instance.
func (r *Registry) List() []*Instance {
	r.mu.RLock()
	defer r.mu.RUnlock()
	out := make([]*Instance, 0, len(r.instances))
	for _, inst := range r.instances {
		out = append(out, cloneInstance(inst))
	}
	return out
}

// RecordPing updates instanceID's last-seen timestamp (PRD §05 heartbeat)
// to now. A no-op if the instance isn't registered — a ping racing a Remove
// (or one that never registered at all) has nothing to record against.
func (r *Registry) RecordPing(instanceID string, now time.Time) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if _, ok := r.instances[instanceID]; !ok {
		return
	}
	r.lastPingAt[instanceID] = now.UTC()
}

// IsResponsive reports whether instanceID has been heard from recently
// enough to not be considered unresponsive, as of now. An instance that has
// never sent a ping yet (the ~10s window right after register, before its
// first ping fires) is judged against ConnectedSince instead, so it isn't
// spuriously marked unresponsive before it's had a chance to ping at all.
// An unknown instanceID is reported responsive — this method judges
// liveness, not registration; List()/the caller is what surfaces absence.
func (r *Registry) IsResponsive(instanceID string, now time.Time) bool {
	r.mu.RLock()
	defer r.mu.RUnlock()
	if last, ok := r.lastPingAt[instanceID]; ok {
		return now.Sub(last) < UnresponsiveThreshold
	}
	if inst, ok := r.instances[instanceID]; ok {
		return now.Sub(inst.ConnectedSince) < UnresponsiveThreshold
	}
	return true
}

// PruneStale removes every instance whose last-seen time (its last ping, or
// ConnectedSince if it never pinged) exceeds PruneAfterSilence as of now,
// and returns the instance_ids removed — this is also what reclaims an
// instance that disconnected and simply never reconnected, since no more
// pings ever arrive for it either.
func (r *Registry) PruneStale(now time.Time) []string {
	r.mu.Lock()
	defer r.mu.Unlock()

	var pruned []string
	for id, inst := range r.instances {
		lastSeen := inst.ConnectedSince
		if last, ok := r.lastPingAt[id]; ok {
			lastSeen = last
		}
		if now.Sub(lastSeen) > PruneAfterSilence {
			pruned = append(pruned, id)
		}
	}
	for _, id := range pruned {
		delete(r.instances, id)
		delete(r.lastPingAt, id)
		delete(r.epochs, id)
	}
	return pruned
}
