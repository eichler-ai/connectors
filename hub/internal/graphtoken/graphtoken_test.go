package graphtoken

import (
	"context"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/hub/internal/store"
	"github.com/eichler-ai/connectors/internal/auth"
)

func keySet(t *testing.T) *auth.KeySet {
	t.Helper()
	pem, err := auth.GenerateKeyPEM()
	if err != nil {
		t.Fatal(err)
	}
	ks, err := auth.ParseKeySet(pem)
	if err != nil {
		t.Fatal(err)
	}
	return ks
}

func TestPutGetRoundTrip(t *testing.T) {
	st := store.NewMemory()
	keys := keySet(t)
	ctx := context.Background()
	const token = "M.C1_BL2.super-secret-refresh-token"

	if err := Put(ctx, st, keys, "u1", token, time.Now()); err != nil {
		t.Fatal(err)
	}
	got, err := Get(ctx, st, keys, "u1")
	if err != nil || got != token {
		t.Fatalf("get: %v %q", err, got)
	}

	// The store itself never sees the plaintext: the ciphertext it holds
	// neither equals nor contains the token.
	raw, err := st.GraphToken(ctx, "u1")
	if err != nil {
		t.Fatal(err)
	}
	if string(raw.Ciphertext) == token || strings.Contains(string(raw.Ciphertext), token) {
		t.Fatalf("plaintext token found in the stored ciphertext: %q", raw.Ciphertext)
	}
}

func TestGetMissingIsErrNotFound(t *testing.T) {
	if _, err := Get(context.Background(), store.NewMemory(), keySet(t), "nobody"); err != store.ErrNotFound {
		t.Fatalf("got %v, want store.ErrNotFound", err)
	}
}

func TestWrongKeyFailsToDecrypt(t *testing.T) {
	st := store.NewMemory()
	ctx := context.Background()
	if err := Put(ctx, st, keySet(t), "u1", "secret", time.Now()); err != nil {
		t.Fatal(err)
	}
	if _, err := Get(ctx, st, keySet(t), "u1"); err == nil {
		t.Fatal("decrypted under a different key set")
	}
}

func TestRotationOverwrites(t *testing.T) {
	st := store.NewMemory()
	keys := keySet(t)
	ctx := context.Background()
	if err := Put(ctx, st, keys, "u1", "first", time.Now()); err != nil {
		t.Fatal(err)
	}
	if err := Put(ctx, st, keys, "u1", "second", time.Now()); err != nil {
		t.Fatal(err)
	}
	got, err := Get(ctx, st, keys, "u1")
	if err != nil || got != "second" {
		t.Fatalf("get after rotation: %v %q", err, got)
	}
}

// TestEncryptionIsNondeterministic guards against a fixed-nonce mistake: two
// encryptions of the same plaintext under the same key must produce
// different ciphertext (AES-GCM's security depends on the nonce never
// repeating for a key).
func TestEncryptionIsNondeterministic(t *testing.T) {
	key := keySet(t).Secret(purpose)
	a, err := encrypt(key, []byte("same plaintext"))
	if err != nil {
		t.Fatal(err)
	}
	b, err := encrypt(key, []byte("same plaintext"))
	if err != nil {
		t.Fatal(err)
	}
	if string(a) == string(b) {
		t.Fatal("two encryptions of the same plaintext produced identical ciphertext")
	}
}
