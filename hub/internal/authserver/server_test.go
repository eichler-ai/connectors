package authserver_test

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/http/cookiejar"
	"net/http/httptest"
	"net/url"
	"regexp"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/hub/internal/authserver"
	"github.com/eichler-ai/connectors/hub/internal/store"
	"github.com/eichler-ai/connectors/internal/auth"
)

const msClientID = "entra-app-id"

type fixture struct {
	t     *testing.T
	as    *authserver.Server
	srv   *httptest.Server
	store *store.Memory
	keys  *auth.KeySet
	oidc  *fakeOIDC
	// client carries the session cookie and never follows redirects, so
	// every hop of the dance is visible.
	client *http.Client
	now    time.Time
	mu     sync.Mutex
}

func (f *fixture) clock() time.Time {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.now
}

func (f *fixture) advance(d time.Duration) {
	f.mu.Lock()
	f.now = f.now.Add(d)
	f.mu.Unlock()
}

func newFixture(t *testing.T) *fixture {
	t.Helper()
	f := &fixture{t: t, now: time.Now(), store: store.NewMemory()}
	f.store.Now = f.clock
	pemData, err := auth.GenerateKeyPEM()
	if err != nil {
		t.Fatal(err)
	}
	f.keys, err = auth.ParseKeySet(pemData)
	if err != nil {
		t.Fatal(err)
	}
	f.oidc = newFakeOIDC(t, msClientID)
	provider := &authserver.OIDCProvider{
		Name:         "microsoft",
		DiscoveryURL: f.oidc.srv.URL + "/common/v2.0/.well-known/openid-configuration",
		ClientID:     msClientID,
		ClientSecret: "s3cret",
		HTTPClient:   f.oidc.srv.Client(),
		Now:          f.clock,
	}
	// The server's URL is only known after it starts, and the issuer must
	// equal it; start on a mux that forwards to the real handler.
	mux := http.NewServeMux()
	f.srv = httptest.NewTLSServer(mux)
	t.Cleanup(f.srv.Close)
	f.as, err = authserver.New(authserver.Options{
		Issuer:            f.srv.URL,
		Store:             f.store,
		Keys:              f.keys,
		Connectors:        []string{"excel", "figma"},
		Provider:          provider,
		Logger:            slog.New(slog.NewTextHandler(io.Discard, nil)),
		HTTPClient:        f.srv.Client(),
		AllowLoopbackCIMD: true,
		Now:               f.clock,
	})
	if err != nil {
		t.Fatal(err)
	}
	f.as.Routes(mux)
	jar, _ := cookiejar.New(nil)
	f.client = &http.Client{
		Transport:     f.srv.Client().Transport,
		Jar:           jar,
		CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },
	}
	return f
}

// signOut forgets the session cookie, so the next signIn goes through the
// provider again.
func (f *fixture) signOut() {
	f.client.Jar, _ = cookiejar.New(nil)
}

func (f *fixture) get(u string) *http.Response {
	f.t.Helper()
	resp, err := f.client.Get(u)
	if err != nil {
		f.t.Fatal(err)
	}
	return resp
}

func (f *fixture) postForm(u string, v url.Values) *http.Response {
	f.t.Helper()
	resp, err := f.client.PostForm(u, v)
	if err != nil {
		f.t.Fatal(err)
	}
	return resp
}

func body(t *testing.T, resp *http.Response) string {
	t.Helper()
	b, _ := io.ReadAll(resp.Body)
	resp.Body.Close()
	return string(b)
}

func (f *fixture) register(redirects ...string) string {
	f.t.Helper()
	b, _ := json.Marshal(map[string]any{"client_name": "Test Client", "redirect_uris": redirects, "token_endpoint_auth_method": "none"})
	resp, err := f.client.Post(f.srv.URL+"/oauth/register", "application/json", strings.NewReader(string(b)))
	if err != nil {
		f.t.Fatal(err)
	}
	var out map[string]any
	txt := body(f.t, resp)
	if resp.StatusCode != 201 || json.Unmarshal([]byte(txt), &out) != nil || out["client_id"] == "" {
		f.t.Fatalf("register: %d %s", resp.StatusCode, txt)
	}
	return out["client_id"].(string)
}

type pkce struct{ verifier, challenge string }

func newPKCE() pkce {
	v := strings.Repeat("v", 43) + "0123456789"
	sum := sha256.Sum256([]byte(v))
	return pkce{verifier: v, challenge: base64.RawURLEncoding.EncodeToString(sum[:])}
}

// authorizeParams is a good request the tests mutate.
func (f *fixture) authorizeParams(clientID, redirect string, p pkce) url.Values {
	return url.Values{
		"response_type":         {"code"},
		"client_id":             {clientID},
		"redirect_uri":          {redirect},
		"state":                 {"st-123"},
		"code_challenge":        {p.challenge},
		"code_challenge_method": {"S256"},
		"resource":              {f.srv.URL + "/excel/mcp"},
		"scope":                 {"excel"},
	}
}

var lsPattern = regexp.MustCompile(`(?:ls=|name="ls" value=")([A-Za-z0-9_-]+)`)

// signIn drives /oauth/authorize → sign-in page → provider → callback →
// consent page, returning the login state id; the session cookie is now
// in the jar.
func (f *fixture) signIn(params url.Values) string {
	f.t.Helper()
	resp := f.get(f.srv.URL + "/oauth/authorize?" + params.Encode())
	page := body(f.t, resp)
	if resp.StatusCode != 200 || !strings.Contains(page, "Sign in with Microsoft") {
		f.t.Fatalf("authorize: %d %s", resp.StatusCode, page)
	}
	m := lsPattern.FindStringSubmatch(page)
	if m == nil {
		f.t.Fatalf("no login state on sign-in page: %s", page)
	}
	ls := m[1]
	resp = f.get(f.srv.URL + "/login/microsoft?ls=" + ls)
	body(f.t, resp)
	loc := resp.Header.Get("Location")
	if resp.StatusCode != 302 || !strings.HasPrefix(loc, f.oidc.srv.URL+"/common/oauth2/v2.0/authorize?") {
		f.t.Fatalf("login start: %d %s", resp.StatusCode, loc)
	}
	resp = f.get(loc)
	body(f.t, resp)
	loc = resp.Header.Get("Location")
	if resp.StatusCode != 302 || !strings.HasPrefix(loc, f.srv.URL+"/login/microsoft/callback?") {
		f.t.Fatalf("provider: %d %s", resp.StatusCode, loc)
	}
	resp = f.get(loc)
	page = body(f.t, resp)
	if resp.StatusCode != 302 || resp.Header.Get("Location") != "/oauth/consent?ls="+ls {
		f.t.Fatalf("callback: %d %s %s", resp.StatusCode, resp.Header.Get("Location"), page)
	}
	var cookie *http.Cookie
	for _, c := range resp.Cookies() {
		if c.Name == "__Host-hub_session" {
			cookie = c
		}
	}
	if cookie == nil || !cookie.Secure || !cookie.HttpOnly || cookie.SameSite != http.SameSiteLaxMode || cookie.Path != "/" || cookie.MaxAge != 3600 {
		f.t.Fatalf("session cookie flags: %+v", cookie)
	}
	resp = f.get(f.srv.URL + "/oauth/consent?ls=" + ls)
	page = body(f.t, resp)
	if resp.StatusCode != 200 || !strings.Contains(page, "Allow Test Client?") && !strings.Contains(page, "Allow ") {
		f.t.Fatalf("consent page: %d %s", resp.StatusCode, page)
	}
	return ls
}

// approve posts the consent and returns the code from the redirect.
func (f *fixture) approve(ls, redirect string) (code string, q url.Values) {
	f.t.Helper()
	resp := f.postForm(f.srv.URL+"/oauth/consent", url.Values{"ls": {ls}, "decision": {"approve"}})
	body(f.t, resp)
	loc, err := url.Parse(resp.Header.Get("Location"))
	if resp.StatusCode != 302 || err != nil || !strings.HasPrefix(loc.String(), redirect) {
		f.t.Fatalf("approve: %d %s", resp.StatusCode, resp.Header.Get("Location"))
	}
	q = loc.Query()
	if q.Get("code") == "" || q.Get("state") != "st-123" || q.Get("iss") != f.srv.URL {
		f.t.Fatalf("redirect params: %v", q)
	}
	return q.Get("code"), q
}

type tokenResponse struct {
	AccessToken  string `json:"access_token"`
	TokenType    string `json:"token_type"`
	ExpiresIn    int    `json:"expires_in"`
	RefreshToken string `json:"refresh_token"`
	Scope        string `json:"scope"`
	Error        string `json:"error"`
	ErrorDesc    string `json:"error_description"`
}

func (f *fixture) token(v url.Values) (int, tokenResponse) {
	f.t.Helper()
	resp := f.postForm(f.srv.URL+"/oauth/token", v)
	if cc := resp.Header.Get("Cache-Control"); cc != "no-store" {
		f.t.Fatalf("token endpoint Cache-Control %q", cc)
	}
	var tr tokenResponse
	txt := body(f.t, resp)
	if err := json.Unmarshal([]byte(txt), &tr); err != nil {
		f.t.Fatalf("token: %d %s", resp.StatusCode, txt)
	}
	return resp.StatusCode, tr
}

func (f *fixture) exchange(clientID, redirect, code string, p pkce) (int, tokenResponse) {
	return f.token(url.Values{
		"grant_type": {"authorization_code"}, "code": {code}, "client_id": {clientID},
		"redirect_uri": {redirect}, "code_verifier": {p.verifier}, "resource": {f.srv.URL + "/excel/mcp"},
	})
}

func (f *fixture) refresh(clientID, rt string) (int, tokenResponse) {
	return f.token(url.Values{"grant_type": {"refresh_token"}, "refresh_token": {rt}, "client_id": {clientID}})
}

func TestMetadataAndJWKS(t *testing.T) {
	f := newFixture(t)
	for _, path := range []string{"/.well-known/oauth-authorization-server", "/.well-known/openid-configuration"} {
		resp := f.get(f.srv.URL + path)
		var doc map[string]any
		if err := json.Unmarshal([]byte(body(t, resp)), &doc); err != nil || resp.StatusCode != 200 {
			t.Fatalf("%s: %d %v", path, resp.StatusCode, err)
		}
		if resp.Header.Get("Access-Control-Allow-Origin") != "*" {
			t.Fatalf("%s: metadata must be readable cross-origin", path)
		}
		want := map[string]string{
			"issuer": f.srv.URL, "authorization_endpoint": f.srv.URL + "/oauth/authorize", "token_endpoint": f.srv.URL + "/oauth/token",
			"registration_endpoint": f.srv.URL + "/oauth/register", "jwks_uri": f.srv.URL + "/oauth/jwks",
		}
		for k, v := range want {
			if doc[k] != v {
				t.Fatalf("%s: %s = %v, want %s", path, k, doc[k], v)
			}
		}
		if fmt.Sprint(doc["code_challenge_methods_supported"]) != "[S256]" || doc["client_id_metadata_document_supported"] != true ||
			fmt.Sprint(doc["token_endpoint_auth_methods_supported"]) != "[none]" || fmt.Sprint(doc["scopes_supported"]) != "[excel figma offline_access]" {
			t.Fatalf("%s: %v", path, doc)
		}
	}
	resp := f.get(f.srv.URL + "/oauth/jwks")
	var set auth.JWKS
	if err := json.Unmarshal([]byte(body(t, resp)), &set); err != nil || len(set.Keys) != 1 || set.Keys[0].Kid != f.keys.KID() {
		t.Fatalf("jwks: %v %+v", err, set)
	}
}

func TestFullCodeFlowWithRefreshRotation(t *testing.T) {
	f := newFixture(t)
	redirect := "http://localhost:7777/callback"
	clientID := f.register(redirect, "https://app.example/cb")
	p := newPKCE()
	ls := f.signIn(f.authorizeParams(clientID, redirect, p))
	code, _ := f.approve(ls, redirect)

	status, tr := f.exchange(clientID, redirect, code, p)
	if status != 200 || tr.TokenType != "Bearer" || tr.ExpiresIn != 900 || tr.RefreshToken == "" || tr.Scope != "excel" {
		t.Fatalf("exchange: %d %+v", status, tr)
	}
	// The access token verifies on the resource-server side with the
	// user's id and the granted scope.
	pr, err := f.as.Verifier().VerifyAccessToken(context.Background(), tr.AccessToken)
	if err != nil || !strings.HasPrefix(pr.UserID, "u_") || strings.Join(pr.Scopes, " ") != "excel" {
		t.Fatalf("verify: %v %+v", err, pr)
	}
	u, err := f.store.UserByIdentity(context.Background(), "microsoft", "oid-1")
	if err != nil || u.ID != pr.UserID || u.Email != "a@example.com" || u.DisplayName != "Ada" || u.Tenant != "tenant-1" {
		t.Fatalf("user record: %v %+v", err, u)
	}
	var claims auth.Claims
	payload, _ := base64.RawURLEncoding.DecodeString(strings.Split(tr.AccessToken, ".")[1])
	_ = json.Unmarshal(payload, &claims)
	if claims.Issuer != f.srv.URL || !claims.Audience.Contains(f.srv.URL) || claims.ClientID != clientID || claims.JTI == "" || claims.Expires-claims.IssuedAt != 900 {
		t.Fatalf("claims: %+v", claims)
	}

	// A replayed code is refused and revokes the tokens it minted.
	if status, tr2 := f.exchange(clientID, redirect, code, p); status != 400 || tr2.Error != "invalid_grant" {
		t.Fatalf("code replay: %d %+v", status, tr2)
	}
	if status, tr2 := f.refresh(clientID, tr.RefreshToken); status != 400 || tr2.Error != "invalid_grant" {
		t.Fatalf("refresh after code replay should be revoked: %d %+v", status, tr2)
	}

	// Fresh grant: rotation chain, then reuse of a rotated token kills the
	// family including the newest token.
	f.signOut()
	ls = f.signIn(f.authorizeParams(clientID, redirect, p))
	code, _ = f.approve(ls, redirect)
	_, tr = f.exchange(clientID, redirect, code, p)
	status, r2 := f.refresh(clientID, tr.RefreshToken)
	if status != 200 || r2.RefreshToken == "" || r2.RefreshToken == tr.RefreshToken || r2.AccessToken == "" {
		t.Fatalf("rotation: %d %+v", status, r2)
	}
	status, r3 := f.refresh(clientID, r2.RefreshToken)
	if status != 200 || r3.RefreshToken == r2.RefreshToken {
		t.Fatalf("second rotation: %d %+v", status, r3)
	}
	if status, x := f.refresh(clientID, tr.RefreshToken); status != 400 || x.Error != "invalid_grant" {
		t.Fatalf("reuse of rotated token: %d %+v", status, x)
	}
	if status, x := f.refresh(clientID, r3.RefreshToken); status != 400 || x.Error != "invalid_grant" {
		t.Fatalf("newest token must be revoked after reuse: %d %+v", status, x)
	}

	// Signed-in users skip the sign-in page: authorize goes straight to
	// consent, and deny returns access_denied with the state.
	resp := f.get(f.srv.URL + "/oauth/authorize?" + f.authorizeParams(clientID, redirect, p).Encode())
	page := body(t, resp)
	if resp.StatusCode != 200 || !strings.Contains(page, "Approve") || strings.Contains(page, "Sign in with Microsoft") {
		t.Fatalf("authorize with session: %d %s", resp.StatusCode, page)
	}
	ls = lsPattern.FindStringSubmatch(page)[1]
	resp = f.postForm(f.srv.URL+"/oauth/consent", url.Values{"ls": {ls}, "decision": {"deny"}})
	body(t, resp)
	loc, _ := url.Parse(resp.Header.Get("Location"))
	if resp.StatusCode != 302 || loc.Query().Get("error") != "access_denied" || loc.Query().Get("state") != "st-123" || loc.Query().Get("code") != "" {
		t.Fatalf("deny: %d %s", resp.StatusCode, loc)
	}
	// The login state is spent either way.
	resp = f.postForm(f.srv.URL+"/oauth/consent", url.Values{"ls": {ls}, "decision": {"approve"}})
	if body(t, resp); resp.StatusCode != 400 {
		t.Fatalf("spent login state reused: %d", resp.StatusCode)
	}
}

func TestAuthorizeRejections(t *testing.T) {
	f := newFixture(t)
	redirect := "https://app.example/cb"
	clientID := f.register(redirect)
	p := newPKCE()

	// Page errors: never redirect when the client or its redirect is wrong.
	pageCases := map[string]func(v url.Values){
		"unknown client":    func(v url.Values) { v.Set("client_id", "dcr_nope") },
		"missing client":    func(v url.Values) { v.Del("client_id") },
		"redirect mismatch": func(v url.Values) { v.Set("redirect_uri", "https://app.example/other") },
		"redirect prefix":   func(v url.Values) { v.Set("redirect_uri", redirect+"?x=1") },
		"missing redirect":  func(v url.Values) { v.Del("redirect_uri") },
	}
	for name, mutate := range pageCases {
		v := f.authorizeParams(clientID, redirect, p)
		mutate(v)
		resp := f.get(f.srv.URL + "/oauth/authorize?" + v.Encode())
		page := body(t, resp)
		if resp.StatusCode != 400 || resp.Header.Get("Location") != "" || !strings.Contains(page, "Eichler Connectors") {
			t.Fatalf("%s: %d loc=%q", name, resp.StatusCode, resp.Header.Get("Location"))
		}
	}
	// Redirect errors carry the error code and state back to the client.
	redirectCases := map[string]struct {
		mutate func(v url.Values)
		err    string
	}{
		"missing state":       {func(v url.Values) { v.Del("state") }, "invalid_request"},
		"missing pkce":        {func(v url.Values) { v.Del("code_challenge") }, "invalid_request"},
		"plain pkce":          {func(v url.Values) { v.Set("code_challenge_method", "plain") }, "invalid_request"},
		"short challenge":     {func(v url.Values) { v.Set("code_challenge", "abc") }, "invalid_request"},
		"token response type": {func(v url.Values) { v.Set("response_type", "token") }, "unsupported_response_type"},
		"missing resource":    {func(v url.Values) { v.Del("resource") }, "invalid_target"},
		"foreign resource":    {func(v url.Values) { v.Set("resource", "https://other.example/mcp") }, "invalid_target"},
		"unknown connector":   {func(v url.Values) { v.Set("resource", f.srv.URL+"/word/mcp") }, "invalid_target"},
		"unknown scope":       {func(v url.Values) { v.Set("scope", "excel admin") }, "invalid_scope"},
	}
	for name, tc := range redirectCases {
		v := f.authorizeParams(clientID, redirect, p)
		tc.mutate(v)
		resp := f.get(f.srv.URL + "/oauth/authorize?" + v.Encode())
		body(t, resp)
		loc, _ := url.Parse(resp.Header.Get("Location"))
		if resp.StatusCode != 302 || !strings.HasPrefix(loc.String(), redirect) || loc.Query().Get("error") != tc.err {
			t.Fatalf("%s: %d %s", name, resp.StatusCode, loc)
		}
		if name != "missing state" && loc.Query().Get("state") != "st-123" {
			t.Fatalf("%s: state not echoed: %s", name, loc)
		}
	}
	// Accepted resource forms: the hub root (all connectors by default),
	// the connector endpoint (that connector), upper-cased host, trailing slash.
	for res, wantScopes := range map[string]string{
		f.srv.URL:                "excel figma",
		f.srv.URL + "/":          "excel figma",
		f.srv.URL + "/figma/mcp": "figma",
		strings.ToUpper(f.srv.URL) + "/excel/mcp": "excel",
	} {
		v := f.authorizeParams(clientID, redirect, p)
		v.Set("resource", res)
		v.Del("scope")
		resp := f.get(f.srv.URL + "/oauth/authorize?" + v.Encode())
		page := body(t, resp)
		if resp.StatusCode != 200 {
			t.Fatalf("resource %s: %d %s", res, resp.StatusCode, page)
		}
		ls := lsPattern.FindStringSubmatch(page)[1]
		st, _ := f.store.LoginState(context.Background(), ls)
		if strings.Join(st.Scope, " ") != wantScopes {
			t.Fatalf("resource %s: default scope %v, want %s", res, st.Scope, wantScopes)
		}
	}
}

func TestTokenRejections(t *testing.T) {
	f := newFixture(t)
	redirect := "http://127.0.0.1:5555/cb"
	clientID := f.register(redirect)
	other := f.register(redirect)
	p := newPKCE()

	fresh := func() string {
		f.signOut()
		ls := f.signIn(f.authorizeParams(clientID, redirect, p))
		code, _ := f.approve(ls, redirect)
		return code
	}
	cases := []struct {
		name   string
		mutate func(v url.Values)
		err    string
	}{
		{"wrong verifier", func(v url.Values) { v.Set("code_verifier", strings.Repeat("x", 50)) }, "invalid_grant"},
		{"short verifier", func(v url.Values) { v.Set("code_verifier", "short") }, "invalid_request"},
		{"wrong client", func(v url.Values) { v.Set("client_id", other) }, "invalid_grant"},
		{"wrong redirect", func(v url.Values) { v.Set("redirect_uri", "http://127.0.0.1:5555/other") }, "invalid_grant"},
		{"foreign resource", func(v url.Values) { v.Set("resource", "https://other.example") }, "invalid_target"},
		{"bad grant", func(v url.Values) { v.Set("grant_type", "password") }, "unsupported_grant_type"},
		{"unknown code", func(v url.Values) { v.Set("code", "nope") }, "invalid_grant"},
	}
	for _, tc := range cases {
		code := fresh()
		v := url.Values{"grant_type": {"authorization_code"}, "code": {code}, "client_id": {clientID}, "redirect_uri": {redirect}, "code_verifier": {p.verifier}}
		tc.mutate(v)
		status, tr := f.token(v)
		if status != 400 || tr.Error != tc.err || tr.AccessToken != "" {
			t.Fatalf("%s: %d %+v", tc.name, status, tr)
		}
	}
	// Expired code.
	code := fresh()
	f.advance(6 * time.Minute)
	if status, tr := f.exchange(clientID, redirect, code, p); status != 400 || tr.Error != "invalid_grant" {
		t.Fatalf("expired code: %d %+v", status, tr)
	}
	// Refresh: wrong client, scope escalation, expiry, garbage.
	f.advance(-6 * time.Minute)
	_, tr := f.exchange(clientID, redirect, fresh(), p)
	if status, x := f.refresh(other, tr.RefreshToken); status != 400 || x.Error != "invalid_grant" {
		t.Fatalf("refresh from another client: %d %+v", status, x)
	}
	_, tr = f.exchange(clientID, redirect, fresh(), p)
	if status, x := f.token(url.Values{"grant_type": {"refresh_token"}, "refresh_token": {tr.RefreshToken}, "client_id": {clientID}, "scope": {"excel figma"}}); status != 400 || x.Error != "invalid_grant" {
		t.Fatalf("scope escalation on refresh: %d %+v", status, x)
	}
	_, tr = f.exchange(clientID, redirect, fresh(), p)
	f.advance(31 * 24 * time.Hour)
	if status, x := f.refresh(clientID, tr.RefreshToken); status != 400 || x.Error != "invalid_grant" {
		t.Fatalf("expired refresh token: %d %+v", status, x)
	}
	if status, x := f.refresh(clientID, "garbage"); status != 400 || x.Error != "invalid_grant" {
		t.Fatalf("garbage refresh token: %d %+v", status, x)
	}
	// An access token issued before the clock jump is expired now.
	if _, err := f.as.Verifier().VerifyAccessToken(context.Background(), tr.AccessToken); err == nil {
		t.Fatal("expired access token verified")
	}
}

func TestRevokeUserKillSwitch(t *testing.T) {
	f := newFixture(t)
	redirect := "https://app.example/cb"
	clientID := f.register(redirect)
	p := newPKCE()
	ls := f.signIn(f.authorizeParams(clientID, redirect, p))
	code, _ := f.approve(ls, redirect)
	_, tr := f.exchange(clientID, redirect, code, p)
	pr, _ := f.as.Verifier().VerifyAccessToken(context.Background(), tr.AccessToken)
	if n, err := f.store.RevokeUser(context.Background(), pr.UserID); err != nil || n != 1 {
		t.Fatalf("revoke: %d %v", n, err)
	}
	if status, x := f.refresh(clientID, tr.RefreshToken); status != 400 || x.Error != "invalid_grant" {
		t.Fatalf("refresh after revoke-user: %d %+v", status, x)
	}
}

func TestIDTokenValidation(t *testing.T) {
	for name, fault := range map[string]func(o *fakeOIDC){
		"nonce":  func(o *fakeOIDC) { o.wrongNonce = true },
		"aud":    func(o *fakeOIDC) { o.wrongAud = true },
		"tenant": func(o *fakeOIDC) { o.wrongTenant = true },
	} {
		t.Run(name, func(t *testing.T) {
			f := newFixture(t)
			fault(f.oidc)
			redirect := "https://app.example/cb"
			clientID := f.register(redirect)
			v := f.authorizeParams(clientID, redirect, newPKCE())
			resp := f.get(f.srv.URL + "/oauth/authorize?" + v.Encode())
			ls := lsPattern.FindStringSubmatch(body(t, resp))[1]
			resp = f.get(f.srv.URL + "/login/microsoft?ls=" + ls)
			body(t, resp)
			resp = f.get(resp.Header.Get("Location"))
			body(t, resp)
			resp = f.get(resp.Header.Get("Location"))
			page := body(t, resp)
			if resp.StatusCode != 502 || !strings.Contains(page, "Sign-in failed") || len(resp.Cookies()) != 0 {
				t.Fatalf("callback with bad %s: %d %s", name, resp.StatusCode, page)
			}
			if _, err := f.store.UserByIdentity(context.Background(), "microsoft", "oid-1"); err == nil {
				t.Fatal("a user was created from an invalid ID token")
			}
		})
	}
}

func TestProviderKeyRotationAndCallbackReplay(t *testing.T) {
	f := newFixture(t)
	redirect := "https://app.example/cb"
	clientID := f.register(redirect)
	p := newPKCE()
	f.signIn(f.authorizeParams(clientID, redirect, p))
	// The provider rotates its key; the next login sees an unknown kid,
	// refetches the JWKS once, and succeeds.
	f.oidc.kid = "k2"
	f.advance(2 * time.Minute)
	f.signOut()
	f.signIn(f.authorizeParams(clientID, redirect, p))
	if f.oidc.keysFetches != 2 {
		t.Fatalf("jwks fetched %d times, want 2 (initial + rotation)", f.oidc.keysFetches)
	}
	// A callback replayed with its original code finds no nonce.
	v := f.authorizeParams(clientID, redirect, p)
	resp := f.get(f.srv.URL + "/oauth/authorize?" + v.Encode())
	ls := lsPattern.FindStringSubmatch(body(t, resp))[1]
	resp = f.get(f.srv.URL + "/login/microsoft?ls=" + ls)
	body(t, resp)
	resp = f.get(resp.Header.Get("Location"))
	body(t, resp)
	callback := resp.Header.Get("Location")
	resp = f.get(callback)
	body(t, resp)
	if resp.StatusCode != 302 {
		t.Fatalf("callback: %d", resp.StatusCode)
	}
	resp = f.get(callback)
	if body(t, resp); resp.StatusCode != 400 {
		t.Fatalf("replayed callback: %d, want 400", resp.StatusCode)
	}
	// Provider-side error → access_denied back to the client.
	resp = f.get(f.srv.URL + "/oauth/authorize?" + v.Encode())
	ls = lsPattern.FindStringSubmatch(body(t, resp))[1]
	resp = f.get(f.srv.URL + "/login/microsoft/callback?state=" + ls + "&error=login_required")
	body(t, resp)
	loc, _ := url.Parse(resp.Header.Get("Location"))
	if resp.StatusCode != 302 || loc.Query().Get("error") != "access_denied" {
		t.Fatalf("provider error: %d %s", resp.StatusCode, loc)
	}
}

func TestLoginStateAndSessionExpiry(t *testing.T) {
	f := newFixture(t)
	redirect := "https://app.example/cb"
	clientID := f.register(redirect)
	p := newPKCE()
	v := f.authorizeParams(clientID, redirect, p)
	resp := f.get(f.srv.URL + "/oauth/authorize?" + v.Encode())
	ls := lsPattern.FindStringSubmatch(body(t, resp))[1]
	f.advance(11 * time.Minute)
	resp = f.get(f.srv.URL + "/login/microsoft?ls=" + ls)
	if body(t, resp); resp.StatusCode != 400 {
		t.Fatalf("expired login state: %d", resp.StatusCode)
	}
	// Session: sign in, wait past an hour, consent needs a fresh sign-in.
	ls = f.signIn(f.authorizeParams(clientID, redirect, p))
	f.advance(61 * time.Minute)
	resp = f.get(f.srv.URL + "/oauth/authorize?" + v.Encode())
	if page := body(t, resp); !strings.Contains(page, "Sign in with Microsoft") {
		t.Fatalf("expired session still signed in: %s", page)
	}
	resp = f.postForm(f.srv.URL+"/oauth/consent", url.Values{"ls": {ls}, "decision": {"approve"}})
	if body(t, resp); resp.StatusCode != 400 && resp.StatusCode != 401 {
		t.Fatalf("consent with expired session/state: %d", resp.StatusCode)
	}
	// A forged cookie is ignored.
	u, _ := url.Parse(f.srv.URL)
	f.client.Jar.SetCookies(u, []*http.Cookie{{Name: "__Host-hub_session", Value: "eyJ1IjoidV9hdHRhY2tlciJ9.AAAA", Secure: true}})
	resp = f.get(f.srv.URL + "/oauth/authorize?" + v.Encode())
	if page := body(t, resp); !strings.Contains(page, "Sign in with Microsoft") {
		t.Fatal("forged session cookie accepted")
	}
}

func TestRegisterValidationAndRateLimit(t *testing.T) {
	f := newFixture(t)
	post := func(doc string) (int, map[string]any) {
		resp, err := f.client.Post(f.srv.URL+"/oauth/register", "application/json", strings.NewReader(doc))
		if err != nil {
			t.Fatal(err)
		}
		var out map[string]any
		_ = json.Unmarshal([]byte(body(t, resp)), &out)
		return resp.StatusCode, out
	}
	bad := map[string]string{
		"no redirects":      `{"client_name":"x"}`,
		"http non-local":    `{"redirect_uris":["http://app.example/cb"]}`,
		"custom scheme":     `{"redirect_uris":["myapp://cb"]}`,
		"fragment":          `{"redirect_uris":["https://app.example/cb#frag"]}`,
		"secret client":     `{"redirect_uris":["https://app.example/cb"],"token_endpoint_auth_method":"client_secret_post"}`,
		"implicit":          `{"redirect_uris":["https://app.example/cb"],"grant_types":["implicit"]}`,
		"token response":    `{"redirect_uris":["https://app.example/cb"],"response_types":["token"]}`,
		"not json":          `redirect_uris=x`,
		"too many":          `{"redirect_uris":[` + strings.Repeat(`"https://a.example/cb",`, 11) + `"https://a.example/cb"]}`,
		"name too long":     `{"redirect_uris":["https://app.example/cb"],"client_name":"` + strings.Repeat("n", 101) + `"}`,
		"oversized":         `{"redirect_uris":["https://app.example/cb"],"client_name":"` + strings.Repeat("n", 20000) + `"}`,
		"loopback with ip6": `{"redirect_uris":["http://[::2]:3/cb"]}`,
	}
	for name, doc := range bad {
		f.advance(time.Minute) // stay under the registration rate limit
		if status, out := post(doc); status != 400 || out["error"] == "" {
			t.Fatalf("%s: %d %v", name, status, out)
		}
	}
	f.advance(time.Minute)
	status, out := post(`{"redirect_uris":["http://[::1]:3/cb","http://localhost/cb","https://app.example/cb"],"client_name":"  Claude  ","grant_types":["authorization_code","refresh_token"],"response_types":["code"]}`)
	if status != 201 || out["client_name"] != "Claude" || out["token_endpoint_auth_method"] != "none" || out["client_id_issued_at"] == nil || out["client_secret"] != nil {
		t.Fatalf("good registration: %d %v", status, out)
	}
	// Rate limit: a burst of 10 per IP within a minute, then 429; a minute
	// later one more is allowed.
	f.advance(time.Hour)
	var statuses []int
	for i := 0; i < 12; i++ {
		st, _ := post(`{"redirect_uris":["https://app.example/cb"]}`)
		statuses = append(statuses, st)
	}
	if fmt.Sprint(statuses) != "[201 201 201 201 201 201 201 201 201 201 429 429]" {
		t.Fatalf("rate limit: %v", statuses)
	}
	f.advance(time.Minute)
	if st, _ := post(`{"redirect_uris":["https://app.example/cb"]}`); st != 201 {
		t.Fatalf("after refill: %d", st)
	}
}

func TestUnusedClientCollection(t *testing.T) {
	f := newFixture(t)
	redirect := "https://app.example/cb"
	used := f.register(redirect)
	unused := f.register(redirect)
	p := newPKCE()
	ls := f.signIn(f.authorizeParams(used, redirect, p))
	f.approve(ls, redirect)
	f.advance(25 * time.Hour)
	n, err := f.store.DeleteUnusedClients(context.Background(), f.clock().Add(-24*time.Hour))
	if err != nil || n != 1 {
		t.Fatalf("collect: %d %v", n, err)
	}
	if _, err := f.store.Client(context.Background(), used); err != nil {
		t.Fatal("used client was collected")
	}
	if _, err := f.store.Client(context.Background(), unused); err == nil {
		t.Fatal("unused client survived")
	}
}

func TestClientIDMetadataDocument(t *testing.T) {
	f := newFixture(t)
	var fetches int
	var mu sync.Mutex
	doc := func(w http.ResponseWriter, id string, redirects []string, extra map[string]any) {
		m := map[string]any{"client_id": id, "client_name": "Claude", "redirect_uris": redirects}
		for k, v := range extra {
			m[k] = v
		}
		w.Header().Set("Cache-Control", "max-age=600")
		_ = json.NewEncoder(w).Encode(m)
	}
	mux := http.NewServeMux()
	var cimdURL string
	mux.HandleFunc("/good.json", func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		fetches++
		mu.Unlock()
		doc(w, cimdURL+"/good.json", []string{"https://claude.ai/api/mcp/auth_callback", "http://localhost:3000/cb"}, nil)
	})
	mux.HandleFunc("/mismatch.json", func(w http.ResponseWriter, r *http.Request) {
		doc(w, "https://elsewhere.example/x.json", []string{"https://claude.ai/cb"}, nil)
	})
	mux.HandleFunc("/badredirect.json", func(w http.ResponseWriter, r *http.Request) {
		doc(w, cimdURL+"/badredirect.json", []string{"http://evil.example/cb"}, nil)
	})
	mux.HandleFunc("/secret.json", func(w http.ResponseWriter, r *http.Request) {
		doc(w, cimdURL+"/secret.json", []string{"https://claude.ai/cb"}, map[string]any{"token_endpoint_auth_method": "client_secret_basic"})
	})
	mux.HandleFunc("/huge.json", func(w http.ResponseWriter, r *http.Request) {
		doc(w, cimdURL+"/huge.json", []string{"https://claude.ai/cb"}, map[string]any{"pad": strings.Repeat("x", 20000)})
	})
	mux.HandleFunc("/redirect.json", func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, cimdURL+"/good.json", http.StatusFound)
	})
	mux.HandleFunc("/notjson.json", func(w http.ResponseWriter, r *http.Request) { _, _ = w.Write([]byte("<html>")) })
	cimdSrv := httptest.NewTLSServer(mux)
	t.Cleanup(cimdSrv.Close)
	cimdURL = cimdSrv.URL
	p := newPKCE()

	// The good document: full flow end to end, consent shows the client
	// name and the redirect host.
	good := cimdURL + "/good.json"
	redirect := "http://localhost:3000/cb"
	ls := f.signIn(f.authorizeParams(good, redirect, p))
	resp := f.get(f.srv.URL + "/oauth/consent?ls=" + ls)
	page := body(t, resp)
	if !strings.Contains(page, "Allow Claude?") || !strings.Contains(page, "localhost:3000") || !strings.Contains(page, "on this computer") {
		t.Fatalf("consent page for CIMD client: %s", page)
	}
	code, _ := f.approve(ls, redirect)
	status, tr := f.exchange(good, redirect, code, p)
	if status != 200 || tr.AccessToken == "" {
		t.Fatalf("CIMD exchange: %d %+v", status, tr)
	}
	// Cached: a second authorize does not refetch.
	f.get(f.srv.URL + "/oauth/authorize?" + f.authorizeParams(good, redirect, p).Encode()).Body.Close()
	if fetches != 1 {
		t.Fatalf("document fetched %d times, want 1 (cached)", fetches)
	}
	f.advance(11 * time.Minute)
	f.get(f.srv.URL + "/oauth/authorize?" + f.authorizeParams(good, redirect, p).Encode()).Body.Close()
	if fetches != 2 {
		t.Fatalf("document fetched %d times after max-age, want 2", fetches)
	}
	// Redirect not in the document (a different port alone would be fine,
	// RFC 8252 §7.3; a different path is not).
	resp = f.get(f.srv.URL + "/oauth/authorize?" + f.authorizeParams(good, "http://localhost:3001/other", p).Encode())
	if body(t, resp); resp.StatusCode != 400 {
		t.Fatalf("unregistered redirect for CIMD client: %d", resp.StatusCode)
	}
	// Rejected documents and ids.
	for name, id := range map[string]string{
		"client_id mismatch": cimdURL + "/mismatch.json",
		"bad redirect":       cimdURL + "/badredirect.json",
		"secret method":      cimdURL + "/secret.json",
		"oversized":          cimdURL + "/huge.json",
		"redirects":          cimdURL + "/redirect.json",
		"not json":           cimdURL + "/notjson.json",
		"missing":            cimdURL + "/missing.json",
		"no path":            "https://app.example",
		"dot segment":        "https://app.example/../x.json",
		"fragment":           "https://app.example/x.json#f",
		"userinfo":           "https://user:pw@app.example/x.json",
		"http":               "http://app.example/x.json",
	} {
		resp := f.get(f.srv.URL + "/oauth/authorize?" + f.authorizeParams(id, "https://claude.ai/cb", p).Encode())
		page := body(t, resp)
		if resp.StatusCode != 400 || resp.Header.Get("Location") != "" || !strings.Contains(page, "Unknown client") {
			t.Fatalf("%s: %d %s", name, resp.StatusCode, page)
		}
	}
}

// TestSSRFGuard: the production fetcher refuses loopback and private
// addresses at dial time.
func TestSSRFGuard(t *testing.T) {
	f := newFixture(t)
	_ = f
	pemData, _ := auth.GenerateKeyPEM()
	keys, _ := auth.ParseKeySet(pemData)
	as, err := authserver.New(authserver.Options{Issuer: "https://hub.example", Store: store.NewMemory(), Keys: keys, Connectors: []string{"excel"},
		Logger: slog.New(slog.NewTextHandler(io.Discard, nil))})
	if err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	as.Routes(mux)
	local := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { t.Error("fetched a loopback document") }))
	defer local.Close()
	req := httptest.NewRequest("GET", "/oauth/authorize?client_id="+url.QueryEscape(local.URL+"/c.json")+"&redirect_uri=https://x.example/cb", nil)
	rr := httptest.NewRecorder()
	mux.ServeHTTP(rr, req)
	if rr.Code != 400 || !strings.Contains(rr.Body.String(), "Unknown client") {
		t.Fatalf("loopback client_id: %d %s", rr.Code, rr.Body.String())
	}
}

// TestLoopbackRedirectAnyPort: RFC 8252 §7.3 — a registered plain-http
// loopback redirect matches any port (Claude Code registers
// http://localhost/callback and listens wherever it can), scheme/host/path
// stay exact, and an https registration still needs an exact match.
func TestLoopbackRedirectAnyPort(t *testing.T) {
	f := newFixture(t)
	clientID := f.register("http://localhost/callback", "http://127.0.0.1/callback", "https://app.example/cb")
	p := newPKCE()
	accepted := []string{"http://localhost:3118/callback", "http://localhost/callback", "http://127.0.0.1:4000/callback", "https://app.example/cb"}
	rejected := []string{"http://localhost:3118/other", "https://localhost:3118/callback", "http://evil.localhost:3118/callback",
		"https://app.example:8443/cb", "https://app.example/cb/", "http://[::1]:3118/callback", "http://localhost:3118/callback?x=1"}
	for _, uri := range accepted {
		resp := f.get(f.srv.URL + "/oauth/authorize?" + f.authorizeParams(clientID, uri, p).Encode())
		if page := body(t, resp); resp.StatusCode != 200 || !strings.Contains(page, "Sign in") {
			t.Fatalf("%s: %d, want the sign-in page", uri, resp.StatusCode)
		}
	}
	for _, uri := range rejected {
		resp := f.get(f.srv.URL + "/oauth/authorize?" + f.authorizeParams(clientID, uri, p).Encode())
		if page := body(t, resp); resp.StatusCode != 400 || !strings.Contains(page, "Redirect not allowed") {
			t.Fatalf("%s: %d, want Redirect not allowed", uri, resp.StatusCode)
		}
	}
	// The code is bound to the port the request used: the exchange must
	// repeat http://localhost:3118/callback, not the registered form.
	redirect := "http://localhost:3118/callback"
	ls := f.signIn(f.authorizeParams(clientID, redirect, p))
	code, _ := f.approve(ls, redirect)
	if status, tr := f.token(url.Values{"grant_type": {"authorization_code"}, "code": {code}, "client_id": {clientID},
		"redirect_uri": {"http://localhost/callback"}, "code_verifier": {p.verifier}}); status != 400 || tr.Error != "invalid_grant" {
		t.Fatalf("exchange with the registered form: %d %+v", status, tr)
	}
	f.signOut()
	ls = f.signIn(f.authorizeParams(clientID, redirect, p))
	code, _ = f.approve(ls, redirect)
	if status, tr := f.exchange(clientID, redirect, code, p); status != 200 || tr.AccessToken == "" {
		t.Fatalf("exchange with the request's URI: %d %+v", status, tr)
	}
}

// TestOfflineAccessScope: "excel offline_access" (what Claude Code sends) is
// accepted, offline_access is not echoed as a granted scope, and a refresh
// token is issued regardless.
func TestOfflineAccessScope(t *testing.T) {
	f := newFixture(t)
	redirect := "http://localhost/callback"
	clientID := f.register(redirect)
	p := newPKCE()
	v := f.authorizeParams(clientID, redirect, p)
	v.Set("scope", "excel offline_access")
	ls := f.signIn(v)
	code, _ := f.approve(ls, redirect)
	status, tr := f.exchange(clientID, redirect, code, p)
	if status != 200 || tr.Scope != "excel" || tr.RefreshToken == "" {
		t.Fatalf("offline_access: %d %+v", status, tr)
	}
	pr, err := f.as.Verifier().VerifyAccessToken(context.Background(), tr.AccessToken)
	if err != nil || strings.Join(pr.Scopes, " ") != "excel" {
		t.Fatalf("token scopes: %v %v", err, pr.Scopes)
	}
	// Alone, it means "the default scopes plus a refresh token".
	f.signOut()
	v.Set("scope", "offline_access")
	ls = f.signIn(v)
	code, _ = f.approve(ls, redirect)
	if status, tr := f.exchange(clientID, redirect, code, p); status != 200 || tr.Scope != "excel" || tr.RefreshToken == "" {
		t.Fatalf("offline_access alone: %d %+v", status, tr)
	}
}

// TestConsentCSPAllowsRedirectOrigin: the consent form's POST ends in a
// 302 to the client's redirect_uri, and browsers apply form-action to that
// redirect, so the consent page's CSP must name the redirect origin — and
// nothing wider. Other pages keep form-action 'self'.
func TestConsentCSPAllowsRedirectOrigin(t *testing.T) {
	f := newFixture(t)
	clientID := f.register("http://localhost/callback", "https://claude.ai/api/mcp/auth_callback")
	p := newPKCE()
	for redirect, origin := range map[string]string{
		"http://localhost:3118/callback":          "http://localhost:3118",
		"https://claude.ai/api/mcp/auth_callback": "https://claude.ai",
	} {
		f.signOut()
		ls := f.signIn(f.authorizeParams(clientID, redirect, p))
		resp := f.get(f.srv.URL + "/oauth/consent?ls=" + ls)
		body(t, resp)
		csp := resp.Header.Get("Content-Security-Policy")
		want := "form-action 'self' " + origin + ";"
		if !strings.Contains(csp, want) || !strings.Contains(csp, "default-src 'none'") || !strings.Contains(csp, "frame-ancestors 'none'") || strings.Contains(csp, "*") {
			t.Fatalf("%s: consent CSP %q, want it to contain %q", redirect, csp, want)
		}
		// Approve still 302s to the redirect_uri.
		f.approve(ls, redirect)
	}
	// The sign-in page (a link, no form) and error page keep 'self' only.
	f.signOut()
	resp := f.get(f.srv.URL + "/oauth/authorize?" + f.authorizeParams(clientID, "http://localhost:3118/callback", p).Encode())
	body(t, resp)
	if csp := resp.Header.Get("Content-Security-Policy"); !strings.Contains(csp, "form-action 'self';") {
		t.Fatalf("sign-in CSP %q", csp)
	}
	resp = f.get(f.srv.URL + "/oauth/consent?ls=nope")
	body(t, resp)
	if csp := resp.Header.Get("Content-Security-Policy"); !strings.Contains(csp, "form-action 'self';") {
		t.Fatalf("error page CSP %q", csp)
	}
}
