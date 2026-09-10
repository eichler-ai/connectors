// Package registry is this server's view of the Rhino instances it is
// connected to (PRD §05): what `register` said, when the last heartbeat came,
// and the memory sample it carried. Under the dial-in design each server holds
// its own registry -- there is no shared primary -- and the entries are owned
// by the dialer's connections: an entry appears when a connection's register
// arrives and goes when that connection ends.
//
// Every mutation keyed by instance_id also carries the registration epoch that
// minted the entry (CONVENTIONS.md "the acting connection's identity travels
// with the action"): a redial can re-register the same instance_id while the
// old connection's teardown is still pending, and an unguarded teardown would
// remove the live replacement.
package registry

import (
	"sort"
	"sync"
	"time"
)

// UnresponsiveThreshold: missed heartbeats before status reads "unresponsive"
// (the plug-in pings every 5 s).
const UnresponsiveThreshold = 30 * time.Second

// PruneAfterSilence: how long a silent instance stays listed before the
// registry drops it and the dialer closes its socket.
const PruneAfterSilence = 5 * time.Minute

// Document is one open Rhino document as `register` reports it.
type Document struct {
	ID     string `json:"document_id"`
	Title  string `json:"title"`
	Path   string `json:"path"`
	Active bool   `json:"active"`
}

// MemorySample rides on the ping (same shape as the Revit connector's).
type MemorySample struct {
	PrivateMB    int64 `json:"private_mb"`
	WorkingSetMB int64 `json:"working_set_mb"`
	ManagedMB    int64 `json:"managed_mb"`
}

// Instance is one connected Rhino.
type Instance struct {
	InstanceID     string        `json:"instance_id"`
	PID            int           `json:"pid"`
	RhinoVersion   string        `json:"rhino_version"`
	Platform       string        `json:"platform"`
	BridgeVersion  string        `json:"bridge_version"`
	Documents      []Document    `json:"documents"`
	ConnectedSince time.Time     `json:"connected_since"`
	Memory         *MemorySample `json:"memory,omitempty"`
}

// Registry is safe for concurrent use.
type Registry struct {
	mu         sync.RWMutex
	instances  map[string]*Instance
	lastPingAt map[string]time.Time
	epochs     map[string]uint64
	nextEpoch  uint64
}

func New() *Registry {
	return &Registry{
		instances:  make(map[string]*Instance),
		lastPingAt: make(map[string]time.Time),
		epochs:     make(map[string]uint64),
	}
}

func clone(inst *Instance) *Instance {
	cp := *inst
	cp.Documents = append([]Document(nil), inst.Documents...)
	if inst.Memory != nil {
		m := *inst.Memory
		cp.Memory = &m
	}
	return &cp
}

// Register inserts or replaces the entry and returns the epoch that owns it.
// A re-register from the SAME connection (a document event) passes the epoch
// it holds and keeps it; a fresh connection passes 0 and mints a new one. A
// non-zero epoch that no longer owns the entry is a STALE connection's
// re-register: it is refused (returns 0, nothing changes), because letting it
// mint a new epoch would hand a displaced connection ownership of the live
// one's entry -- the inversion of the invariant this package exists for
// (review of #281). The connected-since timestamp and memory sample survive a
// same-epoch replace.
func (r *Registry) Register(inst *Instance, epoch uint64, now time.Time) uint64 {
	cp := clone(inst)
	r.mu.Lock()
	defer r.mu.Unlock()
	if epoch != 0 {
		prev, ok := r.instances[cp.InstanceID]
		if !ok || r.epochs[cp.InstanceID] != epoch {
			return 0
		}
		cp.ConnectedSince = prev.ConnectedSince
		cp.Memory = prev.Memory
		r.instances[cp.InstanceID] = cp
		return epoch
	}
	if cp.ConnectedSince.IsZero() {
		cp.ConnectedSince = now.UTC()
	}
	r.nextEpoch++
	r.epochs[cp.InstanceID] = r.nextEpoch
	r.instances[cp.InstanceID] = cp
	r.lastPingAt[cp.InstanceID] = now
	return r.nextEpoch
}

// RemoveIfEpoch drops the entry only if epoch still owns it.
func (r *Registry) RemoveIfEpoch(instanceID string, epoch uint64) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.epochs[instanceID] != epoch {
		return false
	}
	delete(r.instances, instanceID)
	delete(r.lastPingAt, instanceID)
	delete(r.epochs, instanceID)
	return true
}

// RecordPing notes a heartbeat; a nil sample keeps the previous one.
func (r *Registry) RecordPing(instanceID string, epoch uint64, now time.Time, mem *MemorySample) {
	r.mu.Lock()
	defer r.mu.Unlock()
	inst, ok := r.instances[instanceID]
	if !ok || r.epochs[instanceID] != epoch {
		return
	}
	r.lastPingAt[instanceID] = now
	if mem != nil {
		m := *mem
		inst.Memory = &m
	}
}

func (r *Registry) Get(instanceID string) (*Instance, bool) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	inst, ok := r.instances[instanceID]
	if !ok {
		return nil, false
	}
	return clone(inst), true
}

// List returns copies, ordered by connected-since then id, for list_instances.
func (r *Registry) List() []*Instance {
	r.mu.RLock()
	defer r.mu.RUnlock()
	out := make([]*Instance, 0, len(r.instances))
	for _, inst := range r.instances {
		out = append(out, clone(inst))
	}
	sort.Slice(out, func(i, j int) bool {
		if !out[i].ConnectedSince.Equal(out[j].ConnectedSince) {
			return out[i].ConnectedSince.Before(out[j].ConnectedSince)
		}
		return out[i].InstanceID < out[j].InstanceID
	})
	return out
}

// IsResponsive: a heartbeat within UnresponsiveThreshold.
func (r *Registry) IsResponsive(instanceID string, now time.Time) bool {
	r.mu.RLock()
	defer r.mu.RUnlock()
	last, ok := r.lastPingAt[instanceID]
	return ok && now.Sub(last) < UnresponsiveThreshold
}

// PruneStale removes entries silent for PruneAfterSilence and returns their ids
// with the epochs that owned them, so the dialer can close the matching sockets.
func (r *Registry) PruneStale(now time.Time) map[string]uint64 {
	r.mu.Lock()
	defer r.mu.Unlock()
	pruned := map[string]uint64{}
	for id, last := range r.lastPingAt {
		if now.Sub(last) >= PruneAfterSilence {
			pruned[id] = r.epochs[id]
			delete(r.instances, id)
			delete(r.lastPingAt, id)
			delete(r.epochs, id)
		}
	}
	return pruned
}
