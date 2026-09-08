package auth

import (
	"crypto"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rsa"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"math/big"
	"strings"
)

// The compact-JWS and JWK subset the hub needs: ES256 for its own access
// tokens, RS256 for Microsoft's ID tokens. Hand-written rather than a JOSE
// library because the surface is two algorithms, one serialisation and no
// encryption, and every line here is one we can read when a token fails.

// JWK is one key of a JWK Set (RFC 7517), EC or RSA public parameters only.
type JWK struct {
	Kty string `json:"kty"`
	Kid string `json:"kid,omitempty"`
	Use string `json:"use,omitempty"`
	Alg string `json:"alg,omitempty"`
	Crv string `json:"crv,omitempty"`
	X   string `json:"x,omitempty"`
	Y   string `json:"y,omitempty"`
	N   string `json:"n,omitempty"`
	E   string `json:"e,omitempty"`
}

// JWKS is a JWK Set document.
type JWKS struct {
	Keys []JWK `json:"keys"`
}

// PublicKey materialises the JWK.
func (k JWK) PublicKey() (crypto.PublicKey, error) {
	switch k.Kty {
	case "EC":
		if k.Crv != "P-256" {
			return nil, fmt.Errorf("jwk: unsupported curve %q", k.Crv)
		}
		x, err := b64Int(k.X)
		if err != nil {
			return nil, err
		}
		y, err := b64Int(k.Y)
		if err != nil {
			return nil, err
		}
		pub := &ecdsa.PublicKey{Curve: elliptic.P256(), X: x, Y: y}
		if !pub.Curve.IsOnCurve(x, y) {
			return nil, errors.New("jwk: EC point is not on P-256")
		}
		return pub, nil
	case "RSA":
		n, err := b64Int(k.N)
		if err != nil {
			return nil, err
		}
		e, err := b64Int(k.E)
		if err != nil {
			return nil, err
		}
		if !e.IsInt64() || e.Int64() < 3 || n.BitLen() < 2048 {
			return nil, errors.New("jwk: RSA key too weak or malformed")
		}
		return &rsa.PublicKey{N: n, E: int(e.Int64())}, nil
	}
	return nil, fmt.Errorf("jwk: unsupported kty %q", k.Kty)
}

// ECJWK renders a P-256 public key as a JWK with the RFC 7638 thumbprint as
// kid, so the identifier is a function of the key alone and every hub
// instance derives the same one.
func ECJWK(pub *ecdsa.PublicKey) JWK {
	x := pub.X.FillBytes(make([]byte, 32))
	y := pub.Y.FillBytes(make([]byte, 32))
	j := JWK{Kty: "EC", Crv: "P-256", X: b64(x), Y: b64(y), Use: "sig", Alg: "ES256"}
	// RFC 7638: the thumbprint hashes the required members in lexicographic
	// order with no whitespace.
	thumb := sha256.Sum256([]byte(`{"crv":"P-256","kty":"EC","x":"` + j.X + `","y":"` + j.Y + `"}`))
	j.Kid = b64(thumb[:])
	return j
}

// KeyLookup resolves the verification key for a JWS header.
type KeyLookup func(kid, alg string) (crypto.PublicKey, error)

// VerifyJWS checks a compact JWS with ES256 or RS256 and returns its payload.
// The algorithm is taken from the key type the lookup returns, never from
// the header alone, so an attacker cannot downgrade or confuse algorithms.
func VerifyJWS(token string, lookup KeyLookup) ([]byte, error) {
	parts := strings.Split(token, ".")
	if len(parts) != 3 {
		return nil, errors.New("jws: not three segments")
	}
	hdrBytes, err := base64.RawURLEncoding.DecodeString(parts[0])
	if err != nil {
		return nil, errors.New("jws: header is not base64url")
	}
	var hdr struct {
		Alg string `json:"alg"`
		Kid string `json:"kid"`
		Typ string `json:"typ"`
	}
	if err := json.Unmarshal(hdrBytes, &hdr); err != nil {
		return nil, errors.New("jws: header is not JSON")
	}
	key, err := lookup(hdr.Kid, hdr.Alg)
	if err != nil {
		return nil, err
	}
	sig, err := base64.RawURLEncoding.DecodeString(parts[2])
	if err != nil {
		return nil, errors.New("jws: signature is not base64url")
	}
	digest := sha256.Sum256([]byte(parts[0] + "." + parts[1]))
	switch k := key.(type) {
	case *ecdsa.PublicKey:
		if hdr.Alg != "ES256" || len(sig) != 64 {
			return nil, errors.New("jws: EC key requires ES256")
		}
		r := new(big.Int).SetBytes(sig[:32])
		s := new(big.Int).SetBytes(sig[32:])
		if !ecdsa.Verify(k, digest[:], r, s) {
			return nil, errors.New("jws: bad signature")
		}
	case *rsa.PublicKey:
		if hdr.Alg != "RS256" {
			return nil, errors.New("jws: RSA key requires RS256")
		}
		if err := rsa.VerifyPKCS1v15(k, crypto.SHA256, digest[:], sig); err != nil {
			return nil, errors.New("jws: bad signature")
		}
	default:
		return nil, errors.New("jws: unsupported key type")
	}
	payload, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		return nil, errors.New("jws: payload is not base64url")
	}
	return payload, nil
}

// signES256 produces a compact JWS over payload with the given key and kid.
func signES256(priv *ecdsa.PrivateKey, kid string, payload []byte) (string, error) {
	hdr, _ := json.Marshal(map[string]string{"alg": "ES256", "typ": "JWT", "kid": kid})
	signingInput := b64(hdr) + "." + b64(payload)
	digest := sha256.Sum256([]byte(signingInput))
	r, s, err := ecdsa.Sign(randReader, priv, digest[:])
	if err != nil {
		return "", err
	}
	sig := make([]byte, 64)
	r.FillBytes(sig[:32])
	s.FillBytes(sig[32:])
	return signingInput + "." + b64(sig), nil
}

func b64(b []byte) string { return base64.RawURLEncoding.EncodeToString(b) }

func b64Int(s string) (*big.Int, error) {
	b, err := base64.RawURLEncoding.DecodeString(s)
	if err != nil || len(b) == 0 {
		return nil, errors.New("jwk: parameter is not base64url")
	}
	return new(big.Int).SetBytes(b), nil
}

// Audience accepts the `aud` claim as either a string or an array (RFC 7519
// §4.1.3 allows both).
type Audience []string

func (a *Audience) UnmarshalJSON(b []byte) error {
	var one string
	if err := json.Unmarshal(b, &one); err == nil {
		*a = Audience{one}
		return nil
	}
	var many []string
	if err := json.Unmarshal(b, &many); err != nil {
		return errors.New("aud is neither a string nor an array")
	}
	*a = many
	return nil
}

func (a Audience) Contains(s string) bool {
	for _, v := range a {
		if v == s {
			return true
		}
	}
	return false
}
