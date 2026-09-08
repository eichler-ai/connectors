package authserver_test

import (
	"context"
	"strings"
	"testing"

	"github.com/eichler-ai/connectors/hub/internal/graphtoken"
	"github.com/eichler-ai/connectors/hub/internal/store"
)

// TestSignInStoresEncryptedGraphToken covers the RFC excel/docs/rfc-graph-
// create-and-open.md §3.1 flow end to end through the real login callback:
// a Microsoft refresh token that comes back alongside the id_token is
// captured, stored encrypted (never as plaintext), readable back through
// graphtoken.Get with the server's own key set, and gone after RevokeUser —
// the §13 kill switch must cover Graph the same as bridge/refresh tokens.
func TestSignInStoresEncryptedGraphToken(t *testing.T) {
	f := newFixture(t)
	f.oidc.refreshToken = "M.C1_BL2.graph-refresh-secret"
	redirect := "https://app.example/cb"
	clientID := f.register(redirect)
	p := newPKCE()
	ls := f.signIn(f.authorizeParams(clientID, redirect, p))
	code, _ := f.approve(ls, redirect)
	_, tr := f.exchange(clientID, redirect, code, p)
	pr, err := f.as.Verifier().VerifyAccessToken(context.Background(), tr.AccessToken)
	if err != nil {
		t.Fatal(err)
	}

	raw, err := f.store.GraphToken(context.Background(), pr.UserID)
	if err != nil {
		t.Fatalf("graph token not stored: %v", err)
	}
	if string(raw.Ciphertext) == f.oidc.refreshToken || strings.Contains(string(raw.Ciphertext), f.oidc.refreshToken) {
		t.Fatalf("the plaintext refresh token is present in the stored record: %q", raw.Ciphertext)
	}
	got, err := graphtoken.Get(context.Background(), f.store, f.keys, pr.UserID)
	if err != nil || got != f.oidc.refreshToken {
		t.Fatalf("decrypted graph token: %v %q, want %q", err, got, f.oidc.refreshToken)
	}

	if n, err := f.store.RevokeUser(context.Background(), pr.UserID); err != nil || n < 1 {
		t.Fatalf("revoke user: %d %v", n, err)
	}
	if _, err := f.store.GraphToken(context.Background(), pr.UserID); err != store.ErrNotFound {
		t.Fatalf("graph token survived revoke user: %v", err)
	}
}

// TestSignInWithoutGraphRefreshTokenStillSignsIn: a provider response that
// carries no refresh_token (fakeOIDC's default) must not fail the sign-in —
// create_workbook is where a missing grant is reported (graph-not-
// connected), not login.
func TestSignInWithoutGraphRefreshTokenStillSignsIn(t *testing.T) {
	f := newFixture(t)
	redirect := "https://app.example/cb"
	clientID := f.register(redirect)
	p := newPKCE()
	ls := f.signIn(f.authorizeParams(clientID, redirect, p))
	code, _ := f.approve(ls, redirect)
	status, tr := f.exchange(clientID, redirect, code, p)
	if status != 200 || tr.AccessToken == "" {
		t.Fatalf("sign-in without a graph refresh token: %d %+v", status, tr)
	}
	pr, err := f.as.Verifier().VerifyAccessToken(context.Background(), tr.AccessToken)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.GraphToken(context.Background(), pr.UserID); err != store.ErrNotFound {
		t.Fatalf("a graph token was stored despite the provider returning none: %v", err)
	}
}
