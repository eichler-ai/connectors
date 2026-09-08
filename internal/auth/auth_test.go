package auth

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestDevTokenIsBridgeOnly(t *testing.T) {
	if _, err := NewDevToken("short"); err == nil {
		t.Fatal("a short token was accepted")
	}
	d, err := NewDevToken("correct-horse-battery-staple")
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	pb, err := d.VerifyBridgeToken(ctx, "correct-horse-battery-staple")
	if err != nil || pb.UserID == "" || pb.UserID != d.UserID() {
		t.Fatalf("valid bridge token: %v %+v", err, pb)
	}
	// Retired from /mcp in phase 1: the same value is not an access token.
	if _, err := d.VerifyAccessToken(ctx, "correct-horse-battery-staple"); !errors.Is(err, ErrInvalidToken) {
		t.Fatalf("dev token accepted as an access token: %v", err)
	}
	for _, bad := range []string{"", "correct-horse-battery-stapl", "correct-horse-battery-staple "} {
		if _, err := d.VerifyBridgeToken(ctx, bad); !errors.Is(err, ErrInvalidToken) {
			t.Fatalf("token %q: %v", bad, err)
		}
	}
	d3, _ := NewDevToken("another-token-entirely-1")
	if d3.UserID() == d.UserID() {
		t.Fatal("different tokens share a user id")
	}
}

func newKeys(t *testing.T) *KeySet {
	t.Helper()
	pemData, err := GenerateKeyPEM()
	if err != nil {
		t.Fatal(err)
	}
	ks, err := ParseKeySet(pemData)
	if err != nil {
		t.Fatal(err)
	}
	return ks
}

func mint(t *testing.T, ks *KeySet, c Claims) string {
	t.Helper()
	tok, err := ks.Sign(c)
	if err != nil {
		t.Fatal(err)
	}
	return tok
}

const issuer = "https://hub.example"

func goodClaims(now time.Time) Claims {
	return Claims{Issuer: issuer, Subject: "u_1", Audience: Audience{issuer}, Scope: "excel figma", ClientID: "c", JTI: "j", IssuedAt: now.Unix(), Expires: now.Add(AccessTokenTTL).Unix()}
}

func TestJWTVerifier(t *testing.T) {
	ks := newKeys(t)
	now := time.Now()
	v := &JWTVerifier{Keys: ks, Issuer: issuer, Audience: issuer}
	ctx := context.Background()

	p, err := v.VerifyAccessToken(ctx, mint(t, ks, goodClaims(now)))
	if err != nil || p.UserID != "u_1" || strings.Join(p.Scopes, ",") != "excel,figma" || p.Expires.IsZero() {
		t.Fatalf("good token: %v %+v", err, p)
	}
	if _, err := v.VerifyBridgeToken(ctx, mint(t, ks, goodClaims(now))); !errors.Is(err, ErrInvalidToken) {
		t.Fatal("an access token was accepted as a bridge token")
	}

	bad := map[string]Claims{}
	c := goodClaims(now)
	c.Expires = now.Add(-time.Second).Unix()
	bad["expired"] = c
	c = goodClaims(now)
	c.Audience = Audience{"https://other.example"}
	bad["wrong audience"] = c
	c = goodClaims(now)
	c.Issuer = "https://other.example"
	bad["wrong issuer"] = c
	c = goodClaims(now)
	c.Subject = ""
	bad["no subject"] = c
	c = goodClaims(now)
	c.Expires = 0
	bad["no expiry"] = c
	for name, c := range bad {
		if _, err := v.VerifyAccessToken(ctx, mint(t, ks, c)); !errors.Is(err, ErrInvalidToken) {
			t.Fatalf("%s: accepted (%v)", name, err)
		}
	}
	// Audience as an array is fine when it contains the hub.
	c = goodClaims(now)
	c.Audience = Audience{"https://other.example", issuer}
	if _, err := v.VerifyAccessToken(ctx, mint(t, ks, c)); err != nil {
		t.Fatalf("array audience: %v", err)
	}

	// Tampering and garbage.
	tok := mint(t, ks, goodClaims(now))
	parts := strings.Split(tok, ".")
	for name, mangled := range map[string]string{
		"garbage":           "not.a.jwt",
		"two segments":      parts[0] + "." + parts[1],
		"payload swapped":   parts[0] + "." + strings.Split(mint(t, ks, bad["wrong audience"]), ".")[1] + "." + parts[2],
		"signature flipped": parts[0] + "." + parts[1] + "." + flip(parts[2]),
		"alg none":          "eyJhbGciOiJub25lIn0." + parts[1] + ".",
	} {
		if _, err := v.VerifyAccessToken(ctx, mangled); !errors.Is(err, ErrInvalidToken) {
			t.Fatalf("%s: accepted", name)
		}
	}
	// Signed by a key the verifier does not know.
	other := newKeys(t)
	if _, err := v.VerifyAccessToken(ctx, mint(t, other, goodClaims(now))); !errors.Is(err, ErrInvalidToken) {
		t.Fatal("token from an unknown key accepted")
	}
}

func flip(b64 string) string {
	b := []byte(b64)
	if b[5] == 'A' {
		b[5] = 'B'
	} else {
		b[5] = 'A'
	}
	return string(b)
}

// TestKeyRotation: a key set with two keys signs with the first and verifies
// tokens from both, and the JWKS lists both kids.
func TestKeyRotation(t *testing.T) {
	oldPEM, _ := GenerateKeyPEM()
	newPEM, _ := GenerateKeyPEM()
	oldKS, _ := ParseKeySet(oldPEM)
	rotated, err := ParseKeySet(append(newPEM, oldPEM...))
	if err != nil {
		t.Fatal(err)
	}
	if rotated.KID() == oldKS.KID() {
		t.Fatal("the new key should sign after rotation")
	}
	now := time.Now()
	v := &JWTVerifier{Keys: rotated, Issuer: issuer, Audience: issuer}
	for name, ks := range map[string]*KeySet{"old key": oldKS, "new key": rotated} {
		if _, err := v.VerifyAccessToken(context.Background(), mint(t, ks, goodClaims(now))); err != nil {
			t.Fatalf("%s: %v", name, err)
		}
	}
	var set JWKS
	if err := json.Unmarshal(rotated.JWKS(), &set); err != nil || len(set.Keys) != 2 {
		t.Fatalf("jwks: %v %s", err, rotated.JWKS())
	}
	for _, k := range set.Keys {
		if k.Kty != "EC" || k.Crv != "P-256" || k.Alg != "ES256" || k.Kid == "" || k.X == "" || k.Y == "" {
			t.Fatalf("jwk: %+v", k)
		}
		if _, err := k.PublicKey(); err != nil {
			t.Fatal(err)
		}
	}
	// A verifier that only knows the old key rejects the new key's tokens
	// (unknown kid), which is why the old key set must be deployed with the
	// new key prepended before the new key signs anything.
	vOld := &JWTVerifier{Keys: oldKS, Issuer: issuer, Audience: issuer}
	if _, err := vOld.VerifyAccessToken(context.Background(), mint(t, rotated, goodClaims(now))); !errors.Is(err, ErrInvalidToken) {
		t.Fatal("unknown kid accepted")
	}
	// PKCS#8 and openssl's EC PARAMETERS preamble both parse.
	if _, err := ParseKeySet([]byte("-----BEGIN EC PARAMETERS-----\nBggqhkjOPQMBBw==\n-----END EC PARAMETERS-----\n" + string(oldPEM))); err != nil {
		t.Fatal(err)
	}
	if _, err := ParseKeySet([]byte("no key here")); err == nil {
		t.Fatal("empty PEM accepted")
	}
}

func TestSecretIsPurposeBound(t *testing.T) {
	ks := newKeys(t)
	if string(ks.Secret("a")) == string(ks.Secret("b")) || len(ks.Secret("a")) != 32 {
		t.Fatal("derived secrets must differ per purpose")
	}
}

func TestRequireBearer(t *testing.T) {
	ks := newKeys(t)
	now := time.Now()
	v := &JWTVerifier{Keys: ks, Issuer: issuer, Audience: issuer}
	var seen string
	h := RequireBearer(v, BearerOptions{ResourceMetadataURL: issuer + "/excel/.well-known/oauth-protected-resource", Scopes: []string{"excel"}})(
		http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { seen = UserID(r.Context()) }))
	noScope := goodClaims(now)
	noScope.Scope = "figma"
	expired := goodClaims(now)
	expired.Expires = now.Add(-time.Minute).Unix()
	cases := []struct {
		name, header string
		want         int
		challenge    string
	}{
		{"none", "", http.StatusUnauthorized, `resource_metadata="` + issuer + `/excel/.well-known/oauth-protected-resource", scope="excel"`},
		{"wrong", "Bearer wrong", http.StatusUnauthorized, "resource_metadata="},
		{"basic", "Basic abc", http.StatusUnauthorized, "resource_metadata="},
		{"expired", "Bearer " + mint(t, ks, expired), http.StatusUnauthorized, "resource_metadata="},
		{"missing scope", "Bearer " + mint(t, ks, noScope), http.StatusForbidden, `scope="excel"`},
		{"good", "Bearer " + mint(t, ks, goodClaims(now)), http.StatusOK, ""},
	}
	for _, tc := range cases {
		seen = ""
		req := httptest.NewRequest("POST", "/excel/mcp", nil)
		if tc.header != "" {
			req.Header.Set("Authorization", tc.header)
		}
		rr := httptest.NewRecorder()
		h.ServeHTTP(rr, req)
		if rr.Code != tc.want {
			t.Fatalf("%s: status %d, want %d", tc.name, rr.Code, tc.want)
		}
		if got := rr.Header().Get("WWW-Authenticate"); !strings.Contains(got, tc.challenge) || (tc.want != http.StatusOK && !strings.HasPrefix(got, "Bearer ")) {
			t.Fatalf("%s: WWW-Authenticate %q, want it to contain %q", tc.name, got, tc.challenge)
		}
		if tc.want == http.StatusOK && seen != "u_1" {
			t.Fatalf("handler saw user %q", seen)
		}
	}
}
