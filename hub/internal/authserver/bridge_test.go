package authserver_test

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net/http"
	"net/url"
	"regexp"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/hub/internal/authserver"
	"github.com/eichler-ai/connectors/hub/internal/store"
	"github.com/eichler-ai/connectors/internal/auth"
)

// payloadPattern finds the JSON the bridge-token page hands to the add-in:
// the argument of JSON.stringify in the inline script.
var payloadPattern = regexp.MustCompile(`JSON\.stringify\((\{.*?\})\)`)

type bridgePayload struct {
	Token     string `json:"token"`
	Connector string `json:"connector"`
	User      string `json:"user"`
	ExpiresAt string `json:"expires_at"`
}

func sha256hex(s string) string {
	sum := sha256.Sum256([]byte(s))
	return hex.EncodeToString(sum[:])
}

// bridgeSignIn drives /bridge/authorize signed out: it must go straight to
// Microsoft, and the callback must come back to /bridge/authorize with the
// same connector and label.
func (f *fixture) bridgeSignIn(connector, label string) *http.Response {
	f.t.Helper()
	start := f.srv.URL + "/bridge/authorize?" + url.Values{"connector": {connector}, "label": {label}}.Encode()
	resp := f.get(start)
	body(f.t, resp)
	loc := resp.Header.Get("Location")
	if resp.StatusCode != 302 || !strings.HasPrefix(loc, f.oidc.srv.URL+"/common/oauth2/v2.0/authorize?") {
		f.t.Fatalf("bridge authorize signed out: %d %s", resp.StatusCode, loc)
	}
	// The provider's redirect_uri is the ordinary login callback: no new
	// Entra redirect URI for this flow.
	pu, _ := url.Parse(loc)
	if pu.Query().Get("redirect_uri") != f.srv.URL+"/login/microsoft/callback" {
		f.t.Fatalf("provider redirect_uri %q", pu.Query().Get("redirect_uri"))
	}
	// Always force the account chooser (see AuthURL): a multi-account user
	// must not be bound silently to whichever account the browser last used.
	if pu.Query().Get("prompt") != "select_account" {
		f.t.Fatalf("provider prompt %q, want select_account", pu.Query().Get("prompt"))
	}
	resp = f.get(loc)
	body(f.t, resp)
	loc = resp.Header.Get("Location")
	if resp.StatusCode != 302 || !strings.HasPrefix(loc, f.srv.URL+"/login/microsoft/callback?") {
		f.t.Fatalf("provider: %d %s", resp.StatusCode, loc)
	}
	resp = f.get(loc)
	page := body(f.t, resp)
	want := "/bridge/authorize?" + url.Values{"connector": {connector}, "label": {label}}.Encode()
	if resp.StatusCode != 302 || resp.Header.Get("Location") != want {
		f.t.Fatalf("callback: %d %q, want %q (%s)", resp.StatusCode, resp.Header.Get("Location"), want, page)
	}
	return f.get(f.srv.URL + resp.Header.Get("Location"))
}

func TestBridgeAuthorizeMintsAndVerifies(t *testing.T) {
	f := newFixture(t)
	ctx := context.Background()
	resp := f.bridgeSignIn("excel", "Excel on the web")
	page := body(t, resp)
	if resp.StatusCode != 200 || !strings.Contains(page, "messageParent") || !strings.Contains(page, "Ada") {
		t.Fatalf("bridge token page: %d %s", resp.StatusCode, page)
	}
	m := payloadPattern.FindStringSubmatch(page)
	if m == nil {
		t.Fatalf("no payload on the page: %s", page)
	}
	var p bridgePayload
	if err := json.Unmarshal([]byte(m[1]), &p); err != nil || len(p.Token) < 40 || p.Connector != "excel" || p.User != "Ada" {
		t.Fatalf("payload: %v %+v", err, p)
	}
	exp, err := time.Parse(time.RFC3339, p.ExpiresAt)
	if err != nil || exp.Sub(f.clock()) < 89*24*time.Hour || exp.Sub(f.clock()) > 91*24*time.Hour {
		t.Fatalf("expires_at %q: %v", p.ExpiresAt, err)
	}
	// The token appears nowhere but the hand-off, and the page is not
	// cacheable; the CSP admits exactly the nonce'd scripts and Office.js.
	if strings.Count(page, p.Token) != 1 || resp.Header.Get("Cache-Control") != "no-store" {
		t.Fatalf("token exposure: %d occurrences, Cache-Control %q", strings.Count(page, p.Token), resp.Header.Get("Cache-Control"))
	}
	csp := resp.Header.Get("Content-Security-Policy")
	nonce := regexp.MustCompile(`script-src 'nonce-([A-Za-z0-9_-]+)' https://appsforoffice\.microsoft\.com`).FindStringSubmatch(csp)
	if nonce == nil || !strings.Contains(csp, "default-src 'none'") || !strings.Contains(csp, "frame-ancestors 'none'") || strings.Contains(csp, "unsafe-inline'; script") {
		t.Fatalf("bridge token page CSP %q", csp)
	}
	if strings.Count(page, `nonce="`+nonce[1]+`"`) != 2 {
		t.Fatalf("scripts do not carry the CSP nonce %s: %s", nonce[1], page)
	}

	// Stored by hash with the session user's id and the connector; the
	// plaintext is not a key.
	hash := sha256hex(p.Token)
	bt, err := f.store.BridgeToken(ctx, hash)
	if err != nil || bt.Connector != "excel" || bt.Label != "Excel on the web" || !strings.HasPrefix(bt.UserID, "u_") || !bt.LastUsedAt.IsZero() {
		t.Fatalf("stored token: %v %+v", err, bt)
	}
	if _, err := f.store.BridgeToken(ctx, p.Token); !errors.Is(err, store.ErrNotFound) {
		t.Fatal("plaintext token stored as a key")
	}
	u, _ := f.store.UserByIdentity(ctx, "microsoft", "oid-1")
	if bt.UserID != u.ID {
		t.Fatalf("token user %s, want the Microsoft user %s", bt.UserID, u.ID)
	}

	// The verifier resolves it to that user with the connector as scope,
	// and touches last_used_at.
	v := authserver.NewBridgeVerifier(f.store, nil, nil)
	pr, err := v.VerifyBridgeToken(ctx, p.Token)
	if err != nil || pr.UserID != u.ID || strings.Join(pr.Scopes, " ") != "excel" || !pr.Expires.Equal(bt.ExpiresAt) {
		t.Fatalf("verify: %v %+v", err, pr)
	}
	if bt, _ := f.store.BridgeToken(ctx, hash); bt.LastUsedAt.IsZero() {
		t.Fatal("hello did not touch last_used_at")
	}
	for _, bad := range []string{"", "garbage", p.Token + "x", hash} {
		if _, err := v.VerifyBridgeToken(ctx, bad); !errors.Is(err, auth.ErrInvalidToken) {
			t.Fatalf("token %q: %v", bad, err)
		}
	}
	if _, err := v.VerifyAccessToken(ctx, p.Token); !errors.Is(err, auth.ErrInvalidToken) {
		t.Fatal("a bridge token verified as an access token")
	}

	// Signed in already: a second visit mints another token without a
	// sign-in (the pane on a second workbook), for another connector too.
	resp = f.get(f.srv.URL + "/bridge/authorize?connector=figma")
	page = body(t, resp)
	var p2 bridgePayload
	if resp.StatusCode != 200 || json.Unmarshal([]byte(payloadPattern.FindStringSubmatch(page)[1]), &p2) != nil || p2.Token == p.Token || p2.Connector != "figma" {
		t.Fatalf("second mint: %d %+v", resp.StatusCode, p2)
	}
	if pr, err := v.VerifyBridgeToken(ctx, p2.Token); err != nil || strings.Join(pr.Scopes, " ") != "figma" {
		t.Fatalf("figma token: %v %+v", err, pr)
	}

	// Sign-out revokes: 204 whether or not the token exists, and the
	// token no longer verifies.
	revoke := func(tok string) int {
		resp := f.postForm(f.srv.URL+"/bridge/revoke", url.Values{"token": {tok}})
		body(t, resp)
		return resp.StatusCode
	}
	if st := revoke(p.Token); st != 204 {
		t.Fatalf("revoke: %d", st)
	}
	if st := revoke(p.Token); st != 204 {
		t.Fatalf("revoke again: %d", st)
	}
	if st := revoke(""); st != 400 {
		t.Fatalf("revoke without token: %d", st)
	}
	if _, err := v.VerifyBridgeToken(ctx, p.Token); !errors.Is(err, auth.ErrInvalidToken) {
		t.Fatal("revoked token still verifies")
	}
	if _, err := v.VerifyBridgeToken(ctx, p2.Token); err != nil {
		t.Fatal("revoking one token revoked another")
	}

	// The kill switch covers panes.
	if n, err := f.store.RevokeUser(ctx, u.ID); err != nil || n != 1 {
		t.Fatalf("revoke user: %d %v", n, err)
	}
	if _, err := v.VerifyBridgeToken(ctx, p2.Token); !errors.Is(err, auth.ErrInvalidToken) {
		t.Fatal("token survived revoke-user")
	}

	// Expiry: 90 days later a fresh token is refused.
	resp = f.get(f.srv.URL + "/bridge/authorize?connector=excel")
	var p3 bridgePayload
	_ = json.Unmarshal([]byte(payloadPattern.FindStringSubmatch(body(t, resp))[1]), &p3)
	f.advance(91 * 24 * time.Hour)
	// The verifier's own clock is real time; the store's is the fixture's,
	// and the store refuses first.
	if _, err := v.VerifyBridgeToken(ctx, p3.Token); !errors.Is(err, auth.ErrInvalidToken) {
		t.Fatal("expired token verified")
	}
}

func TestBridgeAuthorizeRejections(t *testing.T) {
	f := newFixture(t)
	for name, q := range map[string]string{
		"unknown connector": "connector=word",
		"missing connector": "",
		"path-like":         "connector=excel/mcp",
	} {
		resp := f.get(f.srv.URL + "/bridge/authorize?" + q)
		page := body(t, resp)
		if resp.StatusCode != 400 || resp.Header.Get("Location") != "" || !strings.Contains(page, "Unknown connector") {
			t.Fatalf("%s: %d %s", name, resp.StatusCode, page)
		}
	}
	// A provider error during a pane sign-in renders a page (there is no
	// client to redirect to) and spends the login state.
	resp := f.get(f.srv.URL + "/bridge/authorize?connector=excel")
	body(t, resp)
	pu, _ := url.Parse(resp.Header.Get("Location"))
	state := pu.Query().Get("state")
	resp = f.get(f.srv.URL + "/login/microsoft/callback?state=" + state + "&error=login_required")
	page := body(t, resp)
	if resp.StatusCode != 400 || resp.Header.Get("Location") != "" || !strings.Contains(page, "Sign-in cancelled") {
		t.Fatalf("provider error in pane flow: %d %q %s", resp.StatusCode, resp.Header.Get("Location"), page)
	}
	resp = f.get(f.srv.URL + "/login/microsoft/callback?state=" + state + "&code=x")
	if body(t, resp); resp.StatusCode != 400 {
		t.Fatalf("spent pane login state: %d", resp.StatusCode)
	}
	// The label is bounded and never echoed from the request.
	long := strings.Repeat("L", 200)
	resp = f.bridgeSignIn("excel", long[:80])
	body(t, resp)
	f.signOut()
	resp = f.get(f.srv.URL + "/bridge/authorize?connector=excel&label=" + long)
	body(t, resp)
	pu, _ = url.Parse(resp.Header.Get("Location"))
	ls, err := f.store.LoginState(context.Background(), pu.Query().Get("state"))
	if err != nil || ls.Return != "/bridge/authorize?connector=excel&label="+long[:80] || ls.ClientID != "" {
		t.Fatalf("pane login state: %v %+v", err, ls)
	}
}

// TestBridgeVerifierFallback: with a dev token configured the store is
// asked first and the dev token second; without one, only the store.
func TestBridgeVerifierFallback(t *testing.T) {
	ctx := context.Background()
	st := store.NewMemory()
	dev, _ := auth.NewDevToken("dev-token-0123456789")
	with := authserver.NewBridgeVerifier(st, dev, nil)
	without := authserver.NewBridgeVerifier(st, nil, nil)
	if pr, err := with.VerifyBridgeToken(ctx, "dev-token-0123456789"); err != nil || pr.UserID != dev.UserID() || len(pr.Scopes) != 0 {
		t.Fatalf("dev token through the fallback: %v %+v", err, pr)
	}
	if _, err := without.VerifyBridgeToken(ctx, "dev-token-0123456789"); !errors.Is(err, auth.ErrInvalidToken) {
		t.Fatalf("dev token without a fallback: %v", err)
	}
	now := time.Now()
	_ = st.PutBridgeToken(ctx, store.BridgeToken{Hash: sha256hex("minted"), UserID: "u_1", Connector: "excel", CreatedAt: now, ExpiresAt: now.Add(time.Hour)})
	for _, v := range []*authserver.BridgeVerifier{with, without} {
		if pr, err := v.VerifyBridgeToken(ctx, "minted"); err != nil || pr.UserID != "u_1" {
			t.Fatalf("minted token: %v %+v", err, pr)
		}
	}
	// A store failure is a refusal, not a fall-through to the dev token
	// for a token that is not the dev token.
	broken := authserver.NewBridgeVerifier(failingStore{st}, dev, nil)
	if _, err := broken.VerifyBridgeToken(ctx, "minted"); !errors.Is(err, auth.ErrInvalidToken) {
		t.Fatalf("store failure: %v", err)
	}
}

type failingStore struct{ store.Store }

func (failingStore) BridgeToken(context.Context, string) (store.BridgeToken, error) {
	return store.BridgeToken{}, errors.New("firestore: unavailable")
}
