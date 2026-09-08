package auth

import (
	"context"
	"crypto"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"strings"
	"time"
)

var randReader = rand.Reader

// AccessTokenTTL is how long an access token is valid (PRD §06: 15 minutes).
const AccessTokenTTL = 15 * time.Minute

// KeySet is the hub's signing key plus every key still accepted for
// verification. The secret holds one or more PEM blocks; the FIRST is the
// signing key and all of them are published in the JWKS and honoured by the
// verifier, so a rotation is: prepend a new key, deploy, wait out the
// 15-minute access-token lifetime, drop the old block, deploy again.
type KeySet struct {
	signer    *ecdsa.PrivateKey
	signerKID string
	public    map[string]*ecdsa.PublicKey
	jwks      []byte
}

// ParseKeySet reads one or more P-256 private keys ("EC PRIVATE KEY" or PKCS#8
// "PRIVATE KEY" blocks) from pemData.
func ParseKeySet(pemData []byte) (*KeySet, error) {
	ks := &KeySet{public: map[string]*ecdsa.PublicKey{}}
	var jwks JWKS
	rest := pemData
	for {
		var block *pem.Block
		block, rest = pem.Decode(rest)
		if block == nil {
			break
		}
		var key *ecdsa.PrivateKey
		switch block.Type {
		case "EC PRIVATE KEY":
			k, err := x509.ParseECPrivateKey(block.Bytes)
			if err != nil {
				return nil, fmt.Errorf("jwt signing key: %w", err)
			}
			key = k
		case "PRIVATE KEY":
			k, err := x509.ParsePKCS8PrivateKey(block.Bytes)
			if err != nil {
				return nil, fmt.Errorf("jwt signing key: %w", err)
			}
			ec, ok := k.(*ecdsa.PrivateKey)
			if !ok {
				return nil, errors.New("jwt signing key: PKCS#8 block is not an EC key")
			}
			key = ec
		default:
			// openssl ecparam -genkey emits an "EC PARAMETERS" block first;
			// anything we don't sign with is skipped, not an error.
			continue
		}
		if key.Curve != elliptic.P256() {
			return nil, errors.New("jwt signing key: only P-256 (ES256) is supported")
		}
		j := ECJWK(&key.PublicKey)
		if ks.signer == nil {
			ks.signer, ks.signerKID = key, j.Kid
		}
		if _, dup := ks.public[j.Kid]; !dup {
			ks.public[j.Kid] = &key.PublicKey
			jwks.Keys = append(jwks.Keys, j)
		}
	}
	if ks.signer == nil {
		return nil, errors.New("jwt signing key: no EC private key found in PEM data")
	}
	ks.jwks, _ = json.Marshal(jwks)
	return ks, nil
}

// GenerateKeyPEM makes a fresh P-256 key in the PEM form ParseKeySet reads
// (and deploy.sh's `openssl ecparam -genkey -name prime256v1` produces).
func GenerateKeyPEM() ([]byte, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, err
	}
	der, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		return nil, err
	}
	return pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: der}), nil
}

// JWKS is the public key set document served at /oauth/jwks.
func (ks *KeySet) JWKS() []byte { return ks.jwks }

// KID identifies the current signing key.
func (ks *KeySet) KID() string { return ks.signerKID }

// Secret derives a purpose-bound HMAC key from the signing key, so the
// authorization server's session cookie needs no second secret. Rotating
// the signing key invalidates sessions, which at a one-hour session
// lifetime is a non-event.
func (ks *KeySet) Secret(purpose string) []byte {
	h := crypto.SHA256.New()
	h.Write([]byte("connectors-hub:" + purpose + ":"))
	h.Write(ks.signer.D.Bytes())
	return h.Sum(nil)
}

// Sign produces a JWT over claims with the signing key.
func (ks *KeySet) Sign(claims any) (string, error) {
	payload, err := json.Marshal(claims)
	if err != nil {
		return "", err
	}
	return signES256(ks.signer, ks.signerKID, payload)
}

// lookup implements KeyLookup over the accepted keys.
func (ks *KeySet) lookup(kid, alg string) (crypto.PublicKey, error) {
	pub, ok := ks.public[kid]
	if !ok {
		return nil, errors.New("jwt: unknown kid")
	}
	return pub, nil
}

// Claims is the access token body (PRD §06 plus the brief: iss, aud, sub,
// scope, client_id, jti, iat, exp).
type Claims struct {
	Issuer   string   `json:"iss"`
	Subject  string   `json:"sub"`
	Audience Audience `json:"aud"`
	Scope    string   `json:"scope"`
	ClientID string   `json:"client_id"`
	JTI      string   `json:"jti"`
	IssuedAt int64    `json:"iat"`
	Expires  int64    `json:"exp"`
}

// JWTVerifier is the resource-server side: it verifies access tokens
// against the local key set — no network, no introspection — and checks
// the issuer and audience the hub minted them for.
type JWTVerifier struct {
	Keys     *KeySet
	Issuer   string
	Audience string
	// Now is overridable for expiry tests.
	Now func() time.Time
}

func (v *JWTVerifier) now() time.Time {
	if v.Now != nil {
		return v.Now()
	}
	return time.Now()
}

// VerifyAccessToken implements Authenticator.
func (v *JWTVerifier) VerifyAccessToken(_ context.Context, token string) (Principal, error) {
	payload, err := VerifyJWS(token, v.Keys.lookup)
	if err != nil {
		return Principal{}, ErrInvalidToken
	}
	var c Claims
	if err := json.Unmarshal(payload, &c); err != nil {
		return Principal{}, ErrInvalidToken
	}
	now := v.now()
	switch {
	case c.Issuer != v.Issuer,
		!c.Audience.Contains(v.Audience),
		c.Subject == "",
		c.Expires == 0 || now.After(time.Unix(c.Expires, 0)),
		c.IssuedAt > now.Add(time.Minute).Unix():
		return Principal{}, ErrInvalidToken
	}
	return Principal{UserID: c.Subject, Scopes: strings.Fields(c.Scope), Expires: time.Unix(c.Expires, 0)}, nil
}

// VerifyBridgeToken always fails here: bridge tokens are unit 2's.
func (v *JWTVerifier) VerifyBridgeToken(context.Context, string) (Principal, error) {
	return Principal{}, ErrInvalidToken
}
