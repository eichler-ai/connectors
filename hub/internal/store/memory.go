package store

import (
	"context"
	"sync"
	"time"
)

// Memory is the in-process Store for -dev and tests. Nothing survives a
// restart, which for a dev hub means "sign in again". Bound: every write
// sweeps the expired codes, refresh tokens and login states of its own
// kind, so the maps never hold more than what is currently valid plus the
// registered clients (which the collector trims).
type Memory struct {
	// Now is the clock for expiry sweeps; tests replace it.
	Now func() time.Time

	mu         sync.Mutex
	users      map[string]User // by id
	identities map[string]string
	clients    map[string]Client
	states     map[string]LoginState
	codes      map[string]AuthCode
	refresh    map[string]RefreshToken
}

// NewMemory returns an empty store.
func NewMemory() *Memory {
	return &Memory{
		users:      map[string]User{},
		identities: map[string]string{},
		clients:    map[string]Client{},
		states:     map[string]LoginState{},
		codes:      map[string]AuthCode{},
		refresh:    map[string]RefreshToken{},
	}
}

func identityKey(provider, subject string) string { return provider + ":" + subject }

func (m *Memory) now() time.Time {
	if m.Now != nil {
		return m.Now()
	}
	return time.Now()
}

func (m *Memory) UserByIdentity(_ context.Context, provider, subject string) (User, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	id, ok := m.identities[identityKey(provider, subject)]
	if !ok {
		return User{}, ErrNotFound
	}
	return m.users[id], nil
}

func (m *Memory) PutUser(_ context.Context, u User) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.users[u.ID] = u
	m.identities[identityKey(u.Provider, u.Subject)] = u.ID
	return nil
}

func (m *Memory) PutClient(_ context.Context, c Client) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.clients[c.ID] = c
	return nil
}

func (m *Memory) Client(_ context.Context, id string) (Client, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	c, ok := m.clients[id]
	if !ok {
		return Client{}, ErrNotFound
	}
	return c, nil
}

func (m *Memory) TouchClient(_ context.Context, id string, at time.Time) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	c, ok := m.clients[id]
	if !ok {
		return ErrNotFound
	}
	c.LastUsedAt = at
	m.clients[id] = c
	return nil
}

func (m *Memory) DeleteUnusedClients(_ context.Context, cutoff time.Time) (int, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	n := 0
	for id, c := range m.clients {
		if c.LastUsedAt.IsZero() && c.CreatedAt.Before(cutoff) {
			delete(m.clients, id)
			n++
		}
	}
	return n, nil
}

func (m *Memory) PutLoginState(_ context.Context, s LoginState) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	now := m.now()
	for id, old := range m.states {
		if old.ExpiresAt.Before(now) {
			delete(m.states, id)
		}
	}
	m.states[s.ID] = s
	return nil
}

func (m *Memory) LoginState(_ context.Context, id string) (LoginState, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	s, ok := m.states[id]
	if !ok || s.ExpiresAt.Before(m.now()) {
		return LoginState{}, ErrNotFound
	}
	return s, nil
}

func (m *Memory) DeleteLoginState(_ context.Context, id string) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.states, id)
	return nil
}

func (m *Memory) PutAuthCode(_ context.Context, c AuthCode) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	now := m.now()
	for h, old := range m.codes {
		if old.ExpiresAt.Before(now) {
			delete(m.codes, h)
		}
	}
	m.codes[c.Hash] = c
	return nil
}

func (m *Memory) ConsumeAuthCode(_ context.Context, hash string, now time.Time) (AuthCode, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	c, ok := m.codes[hash]
	if !ok || c.ExpiresAt.Before(now) {
		return AuthCode{}, ErrNotFound
	}
	if !c.UsedAt.IsZero() {
		m.revokeFamilyLocked(c.ID)
		return AuthCode{}, ErrReused
	}
	c.UsedAt = now
	m.codes[hash] = c
	return c, nil
}

func (m *Memory) PutRefreshToken(_ context.Context, t RefreshToken) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.putRefreshLocked(t)
	return nil
}

func (m *Memory) putRefreshLocked(t RefreshToken) {
	now := m.now()
	for h, old := range m.refresh {
		if old.ExpiresAt.Before(now) {
			delete(m.refresh, h)
		}
	}
	m.refresh[t.Hash] = t
}

func (m *Memory) RotateRefreshToken(_ context.Context, oldHash string, now time.Time, next RefreshToken) (RefreshToken, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	old, ok := m.refresh[oldHash]
	if !ok || old.ExpiresAt.Before(now) {
		return RefreshToken{}, ErrNotFound
	}
	if !old.UsedAt.IsZero() {
		m.revokeFamilyLocked(old.FamilyID)
		return RefreshToken{}, ErrReused
	}
	if old.Revoked {
		return RefreshToken{}, ErrRevoked
	}
	old.UsedAt = now
	m.refresh[oldHash] = old
	m.putRefreshLocked(inherit(next, old))
	return old, nil
}

// inherit copies the grant's bindings from the rotated token to its successor.
func inherit(next, old RefreshToken) RefreshToken {
	next.FamilyID, next.UserID, next.ClientID, next.Scope, next.Resource = old.FamilyID, old.UserID, old.ClientID, old.Scope, old.Resource
	return next
}

func (m *Memory) revokeFamilyLocked(familyID string) {
	for h, t := range m.refresh {
		if t.FamilyID == familyID {
			t.Revoked = true
			m.refresh[h] = t
		}
	}
}

func (m *Memory) RevokeFamily(_ context.Context, familyID string) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.revokeFamilyLocked(familyID)
	return nil
}

func (m *Memory) RevokeUser(_ context.Context, userID string) (int, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	n := 0
	for h, t := range m.refresh {
		if t.UserID == userID && !t.Revoked {
			t.Revoked = true
			m.refresh[h] = t
			n++
		}
	}
	return n, nil
}

func (m *Memory) Close() error { return nil }
