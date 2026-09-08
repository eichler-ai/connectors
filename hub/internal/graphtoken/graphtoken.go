// Package graphtoken encrypts the user's Microsoft refresh token at rest
// (RFC excel/docs/rfc-graph-create-and-open.md §3.1, §5) and is the only
// place that ever holds it as plaintext outside the login callback and the
// Graph client's own request. hub/internal/store only ever sees the
// ciphertext this package produces.
package graphtoken

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"errors"
	"fmt"
	"time"

	"github.com/eichler-ai/connectors/hub/internal/store"
	"github.com/eichler-ai/connectors/internal/auth"
)

// purpose is the KeySet.Secret label for the AES-256-GCM key. Distinct from
// every other purpose string in the codebase (the session cookie, the JWT
// signing key itself) so rotating the JWT signing key's D value — which is
// what every purpose is derived from — is the only way any of them change,
// and each purpose's ciphertexts are independent of the others.
const purpose = "graph-token-enc"

// Put encrypts token and stores it for userID, overwriting any previous
// value — a fresh sign-in and a rotation from the Graph client both call
// this the same way. The store never sees plaintext.
func Put(ctx context.Context, st store.Store, keys *auth.KeySet, userID, token string, now time.Time) error {
	ct, err := encrypt(keys.Secret(purpose), []byte(token))
	if err != nil {
		return fmt.Errorf("graphtoken: encrypt: %w", err)
	}
	return st.PutGraphToken(ctx, store.GraphToken{UserID: userID, Ciphertext: ct, UpdatedAt: now})
}

// Get decrypts and returns userID's stored Microsoft refresh token, or
// store.ErrNotFound if they never granted Files access (or RevokeUser
// deleted it) — callers match that with errors.Is against store.ErrNotFound
// directly, so it is returned unwrapped.
func Get(ctx context.Context, st store.Store, keys *auth.KeySet, userID string) (string, error) {
	t, err := st.GraphToken(ctx, userID)
	if err != nil {
		return "", err
	}
	pt, err := decrypt(keys.Secret(purpose), t.Ciphertext)
	if err != nil {
		return "", fmt.Errorf("graphtoken: decrypt: %w", err)
	}
	return string(pt), nil
}

// encrypt seals plaintext under key with AES-256-GCM, prepending the random
// nonce to the returned ciphertext (the standard "nonce || sealed" shape,
// so decrypt needs nothing else stored alongside it).
func encrypt(key, plaintext []byte) ([]byte, error) {
	gcm, err := newGCM(key)
	if err != nil {
		return nil, err
	}
	nonce := make([]byte, gcm.NonceSize())
	if _, err := rand.Read(nonce); err != nil {
		return nil, err
	}
	return gcm.Seal(nonce, nonce, plaintext, nil), nil
}

func decrypt(key, blob []byte) ([]byte, error) {
	gcm, err := newGCM(key)
	if err != nil {
		return nil, err
	}
	if len(blob) < gcm.NonceSize() {
		return nil, errors.New("ciphertext shorter than a nonce")
	}
	nonce, ct := blob[:gcm.NonceSize()], blob[gcm.NonceSize():]
	return gcm.Open(nil, nonce, ct, nil)
}

func newGCM(key []byte) (cipher.AEAD, error) {
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}
	return cipher.NewGCM(block)
}
