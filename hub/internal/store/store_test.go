package store

import (
	"context"
	"errors"
	"os"
	"testing"
	"time"
)

// TestMemoryContract runs the contract against the in-memory store.
// TestFirestoreContract runs the same contract against the Firestore
// emulator when FIRESTORE_EMULATOR_HOST is set (the client library picks the
// variable up itself), so the transactional paths get exercised without a
// GCP project: `gcloud emulators firestore start --host-port=localhost:8900`
// then FIRESTORE_EMULATOR_HOST=localhost:8900 go test ./hub/internal/store/.
func TestMemoryContract(t *testing.T) { contract(t, NewMemory()) }

func TestFirestoreContract(t *testing.T) {
	if os.Getenv("FIRESTORE_EMULATOR_HOST") == "" {
		t.Skip("FIRESTORE_EMULATOR_HOST not set")
	}
	fs, err := NewFirestore(context.Background(), "contract-test", "")
	if err != nil {
		t.Fatal(err)
	}
	defer fs.Close()
	contract(t, fs)
}

func contract(t *testing.T, s Store) {
	ctx := context.Background()
	now := time.Now().Truncate(time.Millisecond)
	suffix := now.Format("150405.000")

	// Users: identity lookup, upsert keeps the id.
	if _, err := s.UserByIdentity(ctx, "microsoft", "oid"+suffix); !errors.Is(err, ErrNotFound) {
		t.Fatalf("missing user: %v", err)
	}
	u := User{ID: "u" + suffix, Provider: "microsoft", Subject: "oid" + suffix, Email: "a@example.com", CreatedAt: now, LastLoginAt: now}
	if err := s.PutUser(ctx, u); err != nil {
		t.Fatal(err)
	}
	u.DisplayName = "Ada"
	if err := s.PutUser(ctx, u); err != nil {
		t.Fatal(err)
	}
	got, err := s.UserByIdentity(ctx, "microsoft", "oid"+suffix)
	if err != nil || got.ID != u.ID || got.DisplayName != "Ada" || !got.CreatedAt.Equal(now) {
		t.Fatalf("user: %v %+v", err, got)
	}
	if got, err := s.User(ctx, u.ID); err != nil || got.Email != "a@example.com" {
		t.Fatalf("user by id: %v %+v", err, got)
	}
	if _, err := s.User(ctx, "nobody"); !errors.Is(err, ErrNotFound) {
		t.Fatalf("missing user by id: %v", err)
	}

	// Clients: touch and collection.
	c := Client{ID: "dcr_" + suffix, Name: "x", RedirectURIs: []string{"https://a/cb"}, CreatedAt: now.Add(-48 * time.Hour)}
	if err := s.PutClient(ctx, c); err != nil {
		t.Fatal(err)
	}
	if got, err := s.Client(ctx, c.ID); err != nil || got.Name != "x" || !got.LastUsedAt.IsZero() {
		t.Fatalf("client: %v %+v", err, got)
	}
	if err := s.TouchClient(ctx, "nope", now); !errors.Is(err, ErrNotFound) {
		t.Fatalf("touch missing: %v", err)
	}
	stale := Client{ID: "dcr_stale_" + suffix, RedirectURIs: []string{"https://a/cb"}, CreatedAt: now.Add(-48 * time.Hour)}
	_ = s.PutClient(ctx, stale)
	if err := s.TouchClient(ctx, c.ID, now); err != nil {
		t.Fatal(err)
	}
	if n, err := s.DeleteUnusedClients(ctx, now.Add(-24*time.Hour)); err != nil || n < 1 {
		t.Fatalf("collect: %d %v", n, err)
	}
	if _, err := s.Client(ctx, c.ID); err != nil {
		t.Fatal("used client collected")
	}
	if _, err := s.Client(ctx, stale.ID); !errors.Is(err, ErrNotFound) {
		t.Fatal("stale client survived")
	}

	// Login state: round trip, delete, expiry.
	ls := LoginState{ID: "ls" + suffix, ClientID: c.ID, Scope: []string{"excel"}, ExpiresAt: now.Add(time.Minute)}
	if err := s.PutLoginState(ctx, ls); err != nil {
		t.Fatal(err)
	}
	if got, err := s.LoginState(ctx, ls.ID); err != nil || got.ClientID != c.ID || len(got.Scope) != 1 {
		t.Fatalf("login state: %v %+v", err, got)
	}
	_ = s.DeleteLoginState(ctx, ls.ID)
	if _, err := s.LoginState(ctx, ls.ID); !errors.Is(err, ErrNotFound) {
		t.Fatal("deleted login state found")
	}
	ls.ExpiresAt = now.Add(-time.Second)
	_ = s.PutLoginState(ctx, ls)
	if _, err := s.LoginState(ctx, ls.ID); !errors.Is(err, ErrNotFound) {
		t.Fatal("expired login state found")
	}

	// Codes: single use; a replay revokes the family.
	ac := AuthCode{Hash: "h" + suffix, ID: "fam" + suffix, ClientID: c.ID, UserID: u.ID, Scope: []string{"excel"}, ExpiresAt: now.Add(time.Minute)}
	if err := s.PutAuthCode(ctx, ac); err != nil {
		t.Fatal(err)
	}
	got1, err := s.ConsumeAuthCode(ctx, ac.Hash, now)
	if err != nil || got1.UserID != u.ID {
		t.Fatalf("consume: %v %+v", err, got1)
	}
	rt := RefreshToken{Hash: "r1" + suffix, FamilyID: ac.ID, UserID: u.ID, ClientID: c.ID, Scope: []string{"excel"}, CreatedAt: now, ExpiresAt: now.Add(time.Hour)}
	if err := s.PutRefreshToken(ctx, rt); err != nil {
		t.Fatal(err)
	}
	if _, err := s.ConsumeAuthCode(ctx, ac.Hash, now); !errors.Is(err, ErrReused) {
		t.Fatalf("code replay: %v", err)
	}
	if _, err := s.RotateRefreshToken(ctx, rt.Hash, now, RefreshToken{Hash: "r2" + suffix, ExpiresAt: now.Add(time.Hour)}); !errors.Is(err, ErrRevoked) {
		t.Fatalf("family after code replay: %v", err)
	}
	if _, err := s.ConsumeAuthCode(ctx, "missing", now); !errors.Is(err, ErrNotFound) {
		t.Fatalf("missing code: %v", err)
	}
	expired := AuthCode{Hash: "hx" + suffix, ID: "famx" + suffix, ExpiresAt: now.Add(-time.Second)}
	_ = s.PutAuthCode(ctx, expired)
	if _, err := s.ConsumeAuthCode(ctx, expired.Hash, now); !errors.Is(err, ErrNotFound) {
		t.Fatalf("expired code: %v", err)
	}

	// Refresh rotation: bindings inherited; reuse revokes the family
	// including the newest token; revoke-user marks the rest.
	rt = RefreshToken{Hash: "a1" + suffix, FamilyID: "famB" + suffix, UserID: u.ID, ClientID: c.ID, Scope: []string{"excel", "figma"}, Resource: "https://hub", CreatedAt: now, ExpiresAt: now.Add(time.Hour)}
	_ = s.PutRefreshToken(ctx, rt)
	old, err := s.RotateRefreshToken(ctx, rt.Hash, now, RefreshToken{Hash: "a2" + suffix, CreatedAt: now, ExpiresAt: now.Add(time.Hour)})
	if err != nil || old.ClientID != c.ID {
		t.Fatalf("rotate: %v %+v", err, old)
	}
	old2, err := s.RotateRefreshToken(ctx, "a2"+suffix, now, RefreshToken{Hash: "a3" + suffix, CreatedAt: now, ExpiresAt: now.Add(time.Hour)})
	if err != nil || old2.FamilyID != rt.FamilyID || old2.UserID != u.ID || len(old2.Scope) != 2 || old2.Resource != "https://hub" {
		t.Fatalf("inherited bindings: %v %+v", err, old2)
	}
	if _, err := s.RotateRefreshToken(ctx, rt.Hash, now, RefreshToken{Hash: "a4" + suffix, ExpiresAt: now.Add(time.Hour)}); !errors.Is(err, ErrReused) {
		t.Fatalf("reuse: %v", err)
	}
	if _, err := s.RotateRefreshToken(ctx, "a3"+suffix, now, RefreshToken{Hash: "a5" + suffix, ExpiresAt: now.Add(time.Hour)}); !errors.Is(err, ErrRevoked) {
		t.Fatalf("newest after reuse: %v", err)
	}
	if _, err := s.RotateRefreshToken(ctx, "a3"+suffix, now.Add(2*time.Hour), RefreshToken{Hash: "a6" + suffix}); !errors.Is(err, ErrNotFound) {
		t.Fatalf("expired: %v", err)
	}
	live := RefreshToken{Hash: "b1" + suffix, FamilyID: "famC" + suffix, UserID: u.ID, ClientID: c.ID, ExpiresAt: now.Add(time.Hour)}
	_ = s.PutRefreshToken(ctx, live)

	// Bridge tokens: round trip, touch, expiry, revoke (idempotent), and
	// the kill switch deletes the user's tokens alongside the refresh
	// tokens it flags.
	bt := BridgeToken{Hash: "bt1" + suffix, UserID: u.ID, Connector: "excel", Label: "Excel/web", CreatedAt: now, ExpiresAt: now.Add(90 * 24 * time.Hour)}
	if err := s.PutBridgeToken(ctx, bt); err != nil {
		t.Fatal(err)
	}
	if got, err := s.BridgeToken(ctx, bt.Hash); err != nil || got.UserID != u.ID || got.Connector != "excel" || got.Label != "Excel/web" || !got.LastUsedAt.IsZero() {
		t.Fatalf("bridge token: %v %+v", err, got)
	}
	if _, err := s.BridgeToken(ctx, "missing"); !errors.Is(err, ErrNotFound) {
		t.Fatalf("missing bridge token: %v", err)
	}
	if err := s.TouchBridgeToken(ctx, bt.Hash, now); err != nil {
		t.Fatal(err)
	}
	if got, _ := s.BridgeToken(ctx, bt.Hash); !got.LastUsedAt.Equal(now) {
		t.Fatalf("touch: last_used_at %v, want %v", got.LastUsedAt, now)
	}
	if err := s.TouchBridgeToken(ctx, "missing", now); !errors.Is(err, ErrNotFound) {
		t.Fatalf("touch missing bridge token: %v", err)
	}
	expiredBT := BridgeToken{Hash: "btx" + suffix, UserID: u.ID, Connector: "excel", CreatedAt: now.Add(-91 * 24 * time.Hour), ExpiresAt: now.Add(-time.Second)}
	_ = s.PutBridgeToken(ctx, expiredBT)
	if _, err := s.BridgeToken(ctx, expiredBT.Hash); !errors.Is(err, ErrNotFound) {
		t.Fatalf("expired bridge token: %v", err)
	}
	gone := BridgeToken{Hash: "btr" + suffix, UserID: u.ID, Connector: "excel", CreatedAt: now, ExpiresAt: now.Add(time.Hour)}
	_ = s.PutBridgeToken(ctx, gone)
	if err := s.RevokeBridgeToken(ctx, gone.Hash); err != nil {
		t.Fatal(err)
	}
	if err := s.RevokeBridgeToken(ctx, gone.Hash); err != nil {
		t.Fatalf("revoking twice: %v", err)
	}
	if _, err := s.BridgeToken(ctx, gone.Hash); !errors.Is(err, ErrNotFound) {
		t.Fatalf("revoked bridge token: %v", err)
	}
	other := BridgeToken{Hash: "bto" + suffix, UserID: "someone-else" + suffix, Connector: "excel", CreatedAt: now, ExpiresAt: now.Add(time.Hour)}
	_ = s.PutBridgeToken(ctx, other)

	// The one live refresh token, plus bt (the memory store may or may not
	// have swept the expired one: either count is right, so revoke the
	// expired token explicitly first to make the number exact).
	_ = s.RevokeBridgeToken(ctx, expiredBT.Hash)
	if n, err := s.RevokeUser(ctx, u.ID); err != nil || n != 2 {
		t.Fatalf("revoke user: %d %v (want the one live refresh token plus one bridge token)", n, err)
	}
	if _, err := s.RotateRefreshToken(ctx, live.Hash, now, RefreshToken{Hash: "b2" + suffix, ExpiresAt: now.Add(time.Hour)}); !errors.Is(err, ErrRevoked) {
		t.Fatalf("after revoke user: %v", err)
	}
	if _, err := s.BridgeToken(ctx, bt.Hash); !errors.Is(err, ErrNotFound) {
		t.Fatalf("bridge token after revoke user: %v", err)
	}
	if _, err := s.BridgeToken(ctx, other.Hash); err != nil {
		t.Fatalf("another user's bridge token was revoked: %v", err)
	}
}
