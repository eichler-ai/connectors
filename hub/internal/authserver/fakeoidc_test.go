package authserver_test

import (
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"net/url"
	"sync"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/internal/auth"
)

// fakeOIDC stands in for Microsoft Entra: discovery with a templated
// issuer, an authorize endpoint that approves instantly, a token endpoint
// that returns an RS256 ID token from its own JWKS. Knobs let a test break
// one claim at a time.
type fakeOIDC struct {
	srv      *httptest.Server
	key      *rsa.PrivateKey
	clientID string
	mu       sync.Mutex
	codes    map[string]codeRecord
	// Identity minted for the next login.
	oid, tid, email, name string
	// Faults.
	wrongNonce, wrongAud, wrongTenant bool
	keysFetches                       int
	kid                               string
}

type codeRecord struct{ nonce, challenge string }

func newFakeOIDC(t *testing.T, clientID string) *fakeOIDC {
	t.Helper()
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	f := &fakeOIDC{key: key, clientID: clientID, codes: map[string]codeRecord{}, oid: "oid-1", tid: "tenant-1", email: "a@example.com", name: "Ada", kid: "k1"}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /common/v2.0/.well-known/openid-configuration", func(w http.ResponseWriter, r *http.Request) {
		_ = json.NewEncoder(w).Encode(map[string]string{
			"issuer":                 f.srv.URL + "/{tenantid}/v2.0",
			"authorization_endpoint": f.srv.URL + "/common/oauth2/v2.0/authorize",
			"token_endpoint":         f.srv.URL + "/common/oauth2/v2.0/token",
			"jwks_uri":               f.srv.URL + "/common/discovery/v2.0/keys",
		})
	})
	mux.HandleFunc("GET /common/oauth2/v2.0/authorize", func(w http.ResponseWriter, r *http.Request) {
		q := r.URL.Query()
		if q.Get("client_id") != clientID || q.Get("response_type") != "code" || q.Get("nonce") == "" || q.Get("code_challenge_method") != "S256" {
			http.Error(w, "bad authorize request: "+r.URL.RawQuery, 400)
			return
		}
		code := "pc-" + q.Get("nonce")[:8]
		f.mu.Lock()
		f.codes[code] = codeRecord{nonce: q.Get("nonce"), challenge: q.Get("code_challenge")}
		f.mu.Unlock()
		u, _ := url.Parse(q.Get("redirect_uri"))
		u.RawQuery = url.Values{"code": {code}, "state": {q.Get("state")}}.Encode()
		http.Redirect(w, r, u.String(), http.StatusFound)
	})
	mux.HandleFunc("POST /common/oauth2/v2.0/token", func(w http.ResponseWriter, r *http.Request) {
		_ = r.ParseForm()
		f.mu.Lock()
		rec, ok := f.codes[r.PostForm.Get("code")]
		delete(f.codes, r.PostForm.Get("code"))
		f.mu.Unlock()
		sum := sha256.Sum256([]byte(r.PostForm.Get("code_verifier")))
		if !ok || r.PostForm.Get("client_secret") != "s3cret" || base64.RawURLEncoding.EncodeToString(sum[:]) != rec.challenge {
			w.WriteHeader(400)
			_ = json.NewEncoder(w).Encode(map[string]string{"error": "invalid_grant", "error_description": "fake: bad code, secret or verifier"})
			return
		}
		nonce, aud, tid := rec.nonce, clientID, f.tid
		if f.wrongNonce {
			nonce = "other"
		}
		if f.wrongAud {
			aud = "other-app"
		}
		if f.wrongTenant {
			tid = "tenant-2"
		}
		claims := map[string]any{
			"iss": f.srv.URL + "/" + f.tid + "/v2.0", "aud": aud, "sub": "sub-" + f.oid, "oid": f.oid, "tid": tid,
			"nonce": nonce, "exp": time.Now().Add(time.Hour).Unix(), "iat": time.Now().Unix(),
			"preferred_username": f.email, "name": f.name,
		}
		_ = json.NewEncoder(w).Encode(map[string]string{"id_token": f.sign(claims), "token_type": "Bearer"})
	})
	mux.HandleFunc("GET /common/discovery/v2.0/keys", func(w http.ResponseWriter, r *http.Request) {
		f.mu.Lock()
		f.keysFetches++
		f.mu.Unlock()
		pub := f.key.PublicKey
		e := []byte{byte(pub.E >> 16), byte(pub.E >> 8), byte(pub.E)}
		_ = json.NewEncoder(w).Encode(auth.JWKS{Keys: []auth.JWK{{Kty: "RSA", Kid: f.kid, Use: "sig", N: base64.RawURLEncoding.EncodeToString(pub.N.Bytes()), E: base64.RawURLEncoding.EncodeToString(e)}}})
	})
	f.srv = httptest.NewTLSServer(mux)
	t.Cleanup(f.srv.Close)
	return f
}

func (f *fakeOIDC) sign(claims map[string]any) string {
	hdr, _ := json.Marshal(map[string]string{"alg": "RS256", "kid": f.kid, "typ": "JWT"})
	body, _ := json.Marshal(claims)
	input := base64.RawURLEncoding.EncodeToString(hdr) + "." + base64.RawURLEncoding.EncodeToString(body)
	digest := sha256.Sum256([]byte(input))
	sig, err := rsa.SignPKCS1v15(rand.Reader, f.key, crypto.SHA256, digest[:])
	if err != nil {
		panic(err)
	}
	return input + "." + base64.RawURLEncoding.EncodeToString(sig)
}
