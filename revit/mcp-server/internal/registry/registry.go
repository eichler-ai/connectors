// Package registry implements the broker's in-memory instance registry —
// the "register" notification handling described in PRD §05, scoped down to
// Phase 1: track instances keyed by instance_id so execute_script can route
// by instance. list_instances, heartbeat, and status derivation are Phase 2
// (PRD §15) and deliberately not implemented here.
package registry

import (
	"sync"
	"time"
)

// Document mirrors one entry of an instance's `register` document list
// (PRD §05/§09) — the fields Phase 1 needs to route and to display; the full
// list_instances shape (workshared flag, etc.) is Phase 2.
type Document struct {
	ID     string `json:"document_id"`
	Title  string `json:"title"`
	Path   string `json:"path"`
	Active bool   `json:"active"`
}

// Instance is the broker's live record of one connected Revit MCP Bridge,
// populated from its `register` notification (PRD §05).
type Instance struct {
	InstanceID   string     `json:"instance_id"`
	PID          int        `json:"pid"`
	RevitVersion string     `json:"revit_version"`
	Documents    []Document `json:"documents"`
	RegisteredAt time.Time  `json:"registered_at"`
}

// Registry is the broker's thread-safe, in-memory instance table, keyed by
// instance_id.
type Registry struct {
	mu        sync.RWMutex
	instances map[string]*Instance
}

// New creates an empty Registry.
func New() *Registry {
	return &Registry{instances: make(map[string]*Instance)}
}

// Register records or replaces the entry for inst.InstanceID. A second
// register for an already-known instance_id (e.g. after a reconnect, per
// PRD §05) overwrites the prior entry outright rather than merging it.
func (r *Registry) Register(inst *Instance) {
	cp := *inst
	cp.Documents = append([]Document(nil), inst.Documents...)
	if cp.RegisteredAt.IsZero() {
		cp.RegisteredAt = time.Now().UTC()
	}

	r.mu.Lock()
	defer r.mu.Unlock()
	r.instances[cp.InstanceID] = &cp
}

// Get returns the current record for instanceID, if any.
func (r *Registry) Get(instanceID string) (*Instance, bool) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	inst, ok := r.instances[instanceID]
	if !ok {
		return nil, false
	}
	cp := *inst
	cp.Documents = append([]Document(nil), inst.Documents...)
	return &cp, true
}

// Remove drops instanceID from the registry. Removing an instance that
// isn't present is a no-op.
func (r *Registry) Remove(instanceID string) {
	r.mu.Lock()
	defer r.mu.Unlock()
	delete(r.instances, instanceID)
}

// List returns a snapshot of every currently-registered instance.
func (r *Registry) List() []*Instance {
	r.mu.RLock()
	defer r.mu.RUnlock()
	out := make([]*Instance, 0, len(r.instances))
	for _, inst := range r.instances {
		cp := *inst
		cp.Documents = append([]Document(nil), inst.Documents...)
		out = append(out, &cp)
	}
	return out
}
