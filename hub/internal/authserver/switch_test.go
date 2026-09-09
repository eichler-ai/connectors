package authserver_test

import (
	"context"
	"encoding/json"
	"net/http"
	"net/url"
	"strings"
	"testing"
)

// TestConsentShowsSignedInIdentityAndSwitchLink: the consent page names the
// account the user is about to approve as and links to /login/switch for
// the same login state.
func TestConsentShowsSignedInIdentityAndSwitchLink(t *testing.T) {
	f := newFixture(t)
	redirect := "https://client.example/cb"
	clientID := f.register(redirect)
	p := newPKCE()
	ls := f.signIn(f.authorizeParams(clientID, redirect, p))
	resp := f.get(f.srv.URL + "/oauth/consent?ls=" + ls)
	page := body(t, resp)
	if resp.StatusCode != 200 || !strings.Contains(page, "Signed in as <strong>a@example.com</strong>") {
		t.Fatalf("consent page missing signed-in identity: %d %s", resp.StatusCode, page)
	}
	if !strings.Contains(page, `href="/login/switch?ls=`+ls+`"`) {
		t.Fatalf("consent page missing switch link: %s", page)
	}
}

// TestLoginSwitchFromConsent: the "different account" link clears the
// session and re-runs the same login state through Microsoft; picking a
// different identity there lands back on consent as that account.
func TestLoginSwitchFromConsent(t *testing.T) {
	f := newFixture(t)
	redirect := "https://client.example/cb"
	clientID := f.register(redirect)
	p := newPKCE()
	ls := f.signIn(f.authorizeParams(clientID, redirect, p))

	// The switch link clears the session cookie...
	resp := f.get(f.srv.URL + "/login/switch?ls=" + ls)
	body(t, resp)
	var cleared *http.Cookie
	for _, c := range resp.Cookies() {
		if c.Name == "__Host-hub_session" {
			cleared = c
		}
	}
	if cleared == nil || cleared.MaxAge >= 0 || cleared.Value != "" {
		t.Fatalf("switch did not expire the session cookie: %+v", cleared)
	}
	// ...and sends the user to Microsoft, not straight back to consent.
	loc := resp.Header.Get("Location")
	if resp.StatusCode != 302 || !strings.HasPrefix(loc, f.oidc.srv.URL+"/common/oauth2/v2.0/authorize?") {
		t.Fatalf("login switch: %d %s", resp.StatusCode, loc)
	}
	pu, _ := url.Parse(loc)
	if pu.Query().Get("prompt") != "select_account" {
		t.Fatalf("login switch prompt: %q", pu.Query().Get("prompt"))
	}

	// A different person picks a different Microsoft account.
	f.oidc.oid, f.oidc.email, f.oidc.name = "oid-2", "b@example.com", "Bob"
	resp = f.get(loc)
	body(t, resp)
	loc = resp.Header.Get("Location")
	if resp.StatusCode != 302 || !strings.HasPrefix(loc, f.srv.URL+"/login/microsoft/callback?") {
		t.Fatalf("provider: %d %s", resp.StatusCode, loc)
	}
	resp = f.get(loc)
	body(t, resp)
	if resp.StatusCode != 302 || resp.Header.Get("Location") != "/oauth/consent?ls="+ls {
		t.Fatalf("callback after switch: %d %s", resp.StatusCode, resp.Header.Get("Location"))
	}

	// Consent now shows Bob, not Ada, and approving binds the code to Bob.
	resp = f.get(f.srv.URL + "/oauth/consent?ls=" + ls)
	page := body(t, resp)
	if resp.StatusCode != 200 || !strings.Contains(page, "Signed in as <strong>b@example.com</strong>") || strings.Contains(page, "a@example.com") {
		t.Fatalf("consent after switch: %d %s", resp.StatusCode, page)
	}
	code, _ := f.approve(ls, redirect)
	status, tr := f.exchange(clientID, redirect, code, p)
	if status != 200 {
		t.Fatalf("exchange after switch: %d %+v", status, tr)
	}
	pr, err := f.as.Verifier().VerifyAccessToken(context.Background(), tr.AccessToken)
	if err != nil {
		t.Fatal(err)
	}
	bob, _ := f.store.UserByIdentity(context.Background(), "microsoft", "oid-2")
	if pr.UserID != bob.ID {
		t.Fatalf("code bound to %s, want Bob %s", pr.UserID, bob.ID)
	}
}

// TestLoginSwitchUnknownLoginState: an expired or unknown ls renders the
// standard expiry page rather than clearing the session and guessing.
func TestLoginSwitchUnknownLoginState(t *testing.T) {
	f := newFixture(t)
	resp := f.get(f.srv.URL + "/login/switch?ls=nope")
	page := body(t, resp)
	if resp.StatusCode != 400 || !strings.Contains(page, "expired") {
		t.Fatalf("login switch with unknown ls: %d %s", resp.StatusCode, page)
	}
}

// TestBridgeAuthorizeSwitchAccount: ?switch=1 with an existing session
// clears it and goes to Microsoft instead of minting immediately; without
// it, the existing fast path (reuse the session, mint right away) still
// works.
func TestBridgeAuthorizeSwitchAccount(t *testing.T) {
	f := newFixture(t)
	resp := f.bridgeSignIn("excel", "")
	body(t, resp)

	// Signed in, no switch param: mints immediately (unchanged fast path).
	resp = f.get(f.srv.URL + "/bridge/authorize?connector=excel")
	page := body(t, resp)
	if resp.StatusCode != 200 || !payloadPattern.MatchString(page) {
		t.Fatalf("fast path without switch: %d %s", resp.StatusCode, page)
	}

	// Signed in, switch=1: does NOT mint. Clears the session and redirects
	// to Microsoft, same as a signed-out request.
	resp = f.get(f.srv.URL + "/bridge/authorize?connector=excel&switch=1")
	body(t, resp)
	var cleared *http.Cookie
	for _, c := range resp.Cookies() {
		if c.Name == "__Host-hub_session" {
			cleared = c
		}
	}
	if cleared == nil || cleared.MaxAge >= 0 {
		t.Fatalf("switch=1 did not expire the session cookie: %+v", cleared)
	}
	loc := resp.Header.Get("Location")
	if resp.StatusCode != 302 || !strings.HasPrefix(loc, f.oidc.srv.URL+"/common/oauth2/v2.0/authorize?") {
		t.Fatalf("bridge authorize switch=1: %d %s", resp.StatusCode, loc)
	}
	pu, _ := url.Parse(loc)
	if pu.Query().Get("prompt") != "select_account" {
		t.Fatalf("bridge switch prompt: %q", pu.Query().Get("prompt"))
	}

	// prompt=select_account (the OAuth-side spelling) does the same thing.
	f.signOut()
	resp = f.bridgeSignIn("excel", "")
	body(t, resp)
	resp = f.get(f.srv.URL + "/bridge/authorize?connector=excel&prompt=select_account")
	body(t, resp)
	loc = resp.Header.Get("Location")
	if resp.StatusCode != 302 || !strings.HasPrefix(loc, f.oidc.srv.URL+"/common/oauth2/v2.0/authorize?") {
		t.Fatalf("bridge authorize prompt=select_account: %d %s", resp.StatusCode, loc)
	}

	// A different account is chosen on the provider, and the pane lands
	// back at /bridge/authorize signed in as it (without switch=1, so the
	// next visit takes the fast path instead of looping back to Microsoft).
	f.oidc.oid, f.oidc.email, f.oidc.name = "oid-3", "c@example.com", "Cara"
	resp = f.get(loc)
	body(t, resp)
	loc = resp.Header.Get("Location")
	resp = f.get(loc)
	body(t, resp)
	returnLoc := resp.Header.Get("Location")
	if resp.StatusCode != 302 || returnLoc != "/bridge/authorize?connector=excel&label=" {
		t.Fatalf("callback return after bridge switch: %d %q", resp.StatusCode, returnLoc)
	}
	resp = f.get(f.srv.URL + returnLoc)
	page = body(t, resp)
	if resp.StatusCode != 200 || !strings.Contains(page, "Cara") {
		t.Fatalf("mint after switch: %d %s", resp.StatusCode, page)
	}
	m := payloadPattern.FindStringSubmatch(page)
	var pl bridgePayload
	if m == nil || json.Unmarshal([]byte(m[1]), &pl) != nil || pl.User != "Cara" {
		t.Fatalf("payload after switch: %+v", pl)
	}
	cara, _ := f.store.UserByIdentity(context.Background(), "microsoft", "oid-3")
	hash := sha256hex(pl.Token)
	bt, err := f.store.BridgeToken(context.Background(), hash)
	if err != nil || bt.UserID != cara.ID {
		t.Fatalf("bridge token after switch: %v %+v", err, bt)
	}
}
