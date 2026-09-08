// Package registry is the hub's live table of bridge connections (PRD §07),
// keyed by {user_id, connector, instance_id}. It is in-process only: v1 runs
// one hub instance, and the single routing seam — Send — is where a fan-out
// to other instances slots in when that stops being true. There is no
// persistence here on purpose; a store appears when a phase needs one.
package registry

import (
	"context"
	"errors"
	"slices"
	"sort"
	"sync"
	"time"

	"github.com/eichler-ai/connectors/hub/protocol"
)

// Key identifies a bridge. Two connections with the same Key are the same
// bridge reconnecting, and the newer one wins.
type Key struct {
	UserID     string
	Connector  string
	InstanceID string
}

// Link is the hub's handle on one connection to a bridge. The WebSocket
// handler implements it; tests use a fake.
type Link interface {
	// Send delivers one message to the bridge. It returns once the message is
	// written or the context ends; it never blocks on the bridge's reply.
	Send(ctx context.Context, msg protocol.Message) error
	// Close ends the connection with a reason the bridge can show.
	Close(reason string)
}

// Bridge is one registered connection with the state hello/register gave it.
// The fields other than Documents are immutable after Register; Documents is
// read through Snapshot so callers never see a torn update.
type Bridge struct {
	Key
	Host            protocol.Host
	BridgeVersion   string
	ProtocolVersion int
	Since           time.Time
	Link            Link

	mu        sync.Mutex
	documents []protocol.Document
}

// Documents returns a copy of the bridge's live documents[].
func (b *Bridge) Documents() []protocol.Document {
	b.mu.Lock()
	defer b.mu.Unlock()
	return slices.Clone(b.documents)
}

// Document finds a document by id, or the active one for id == "".
func (b *Bridge) Document(id string) (protocol.Document, bool) {
	b.mu.Lock()
	defer b.mu.Unlock()
	for _, d := range b.documents {
		if (id == "" && d.Active) || (id != "" && d.ID == id) {
			return d, true
		}
	}
	return protocol.Document{}, false
}

// Registry holds every live bridge for one hub process.
type Registry struct {
	mu      sync.Mutex
	bridges map[Key]*Bridge
}

func New() *Registry { return &Registry{bridges: map[Key]*Bridge{}} }

// ErrNotRegistered is returned by Send for a bridge that is no longer the
// live connection for its key.
var ErrNotRegistered = errors.New("bridge is not registered")

// Register makes b the live connection for its Key and returns the connection
// it displaced, if any. The caller sends `replaced` to the loser and closes
// it — that is a wire concern, kept out of the registry so the newest-wins
// rule is testable without sockets.
func (r *Registry) Register(b *Bridge, docs []protocol.Document) (replaced *Bridge) {
	b.mu.Lock()
	b.documents = slices.Clone(docs)
	b.mu.Unlock()
	r.mu.Lock()
	defer r.mu.Unlock()
	replaced = r.bridges[b.Key]
	r.bridges[b.Key] = b
	return replaced
}

// Unregister removes b only if it is still the live connection for its key.
// A stale connection's teardown can run long after a reconnect has taken the
// key over, so removal is by identity, never by key alone (CONVENTIONS.md,
// "the acting connection's identity travels with the action").
func (r *Registry) Unregister(b *Bridge) (removed bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.bridges[b.Key] != b {
		return false
	}
	delete(r.bridges, b.Key)
	return true
}

// UpdateDocuments replaces b's documents[] from a `register` message.
func (r *Registry) UpdateDocuments(b *Bridge, docs []protocol.Document) {
	b.mu.Lock()
	b.documents = slices.Clone(docs)
	b.mu.Unlock()
}

// Get returns the live bridge for the key.
func (r *Registry) Get(user, connector, instanceID string) (*Bridge, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	b, ok := r.bridges[Key{user, connector, instanceID}]
	return b, ok
}

// List returns the user's live bridges for a connector, oldest first so the
// order is stable between calls.
func (r *Registry) List(user, connector string) []*Bridge {
	r.mu.Lock()
	defer r.mu.Unlock()
	var out []*Bridge
	for k, b := range r.bridges {
		if k.UserID == user && k.Connector == connector {
			out = append(out, b)
		}
	}
	sort.Slice(out, func(i, j int) bool {
		if !out[i].Since.Equal(out[j].Since) {
			return out[i].Since.Before(out[j].Since)
		}
		return out[i].InstanceID < out[j].InstanceID
	})
	return out
}

// Send routes a message to a bridge. This is the seam (§07): today it writes
// to the bridge's own Link after checking b is still the live connection for
// its key; a multi-instance hub would publish to wherever that connection
// lives instead, and nothing above this call would change.
func (r *Registry) Send(ctx context.Context, b *Bridge, msg protocol.Message) error {
	r.mu.Lock()
	live := r.bridges[b.Key] == b
	r.mu.Unlock()
	if !live {
		return ErrNotRegistered
	}
	return b.Link.Send(ctx, msg)
}
