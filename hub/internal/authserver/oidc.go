package authserver

import (
	"context"
	"crypto"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"

	"github.com/eichler-ai/connectors/internal/auth"
)

// OIDCProvider is the upstream identity provider the hub delegates sign-in
// to: Microsoft Entra's `common` authority at launch (PRD §18.1). Written
// against OpenID Connect Core + Discovery so a second provider is another
// value of this type, not another implementation.
type OIDCProvider struct {
	// Name is the identity record's `provider` ("microsoft").
	Name string
	// DiscoveryURL is the provider's openid-configuration document.
	DiscoveryURL string
	ClientID     string
	ClientSecret string
	HTTPClient   *http.Client
	// Now is overridable for tests.
	Now func() time.Time

	mu        sync.Mutex
	disc      *discovery
	discAt    time.Time
	keys      map[string]crypto.PublicKey
	keysAt    time.Time
	keysRetry time.Time
}

// MicrosoftDiscoveryURL is the multi-tenant (work, school and personal)
// Entra v2.0 authority.
const MicrosoftDiscoveryURL = "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration"

type discovery struct {
	Issuer                string `json:"issuer"`
	AuthorizationEndpoint string `json:"authorization_endpoint"`
	TokenEndpoint         string `json:"token_endpoint"`
	JWKSURI               string `json:"jwks_uri"`
}

// Identity is what a completed sign-in yields.
type Identity struct {
	Subject     string
	Tenant      string
	Email       string
	DisplayName string
}

const (
	discoveryTTL    = 24 * time.Hour
	jwksTTL         = 24 * time.Hour
	jwksRetryPeriod = time.Minute
	providerTimeout = 10 * time.Second
)

func (p *OIDCProvider) now() time.Time {
	if p.Now != nil {
		return p.Now()
	}
	return time.Now()
}

func (p *OIDCProvider) client() *http.Client {
	if p.HTTPClient != nil {
		return p.HTTPClient
	}
	return &http.Client{Timeout: providerTimeout}
}

func (p *OIDCProvider) getJSON(ctx context.Context, u string, v any) error {
	ctx, cancel := context.WithTimeout(ctx, providerTimeout)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
	if err != nil {
		return err
	}
	resp, err := p.client().Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("GET %s: HTTP %d", u, resp.StatusCode)
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		return err
	}
	return json.Unmarshal(body, v)
}

func (p *OIDCProvider) discover(ctx context.Context) (*discovery, error) {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.disc != nil && p.now().Sub(p.discAt) < discoveryTTL {
		return p.disc, nil
	}
	var d discovery
	if err := p.getJSON(ctx, p.DiscoveryURL, &d); err != nil {
		return nil, fmt.Errorf("oidc discovery: %w", err)
	}
	if d.AuthorizationEndpoint == "" || d.TokenEndpoint == "" || d.JWKSURI == "" || d.Issuer == "" {
		return nil, errors.New("oidc discovery: document is missing endpoints")
	}
	p.disc, p.discAt = &d, p.now()
	return p.disc, nil
}

// key returns the provider's key for kid, refetching the JWKS when the kid
// is unknown (rotation) at most once a minute (a flood of bad kids must
// not become a flood of fetches).
func (p *OIDCProvider) key(ctx context.Context, jwksURI, kid string) (crypto.PublicKey, error) {
	p.mu.Lock()
	defer p.mu.Unlock()
	now := p.now()
	if k, ok := p.keys[kid]; ok && now.Sub(p.keysAt) < jwksTTL {
		return k, nil
	}
	if now.Before(p.keysRetry) {
		return nil, errors.New("oidc: unknown signing key")
	}
	p.keysRetry = now.Add(jwksRetryPeriod)
	var set auth.JWKS
	if err := p.getJSON(ctx, jwksURI, &set); err != nil {
		return nil, fmt.Errorf("oidc jwks: %w", err)
	}
	keys := map[string]crypto.PublicKey{}
	for _, j := range set.Keys {
		if pk, err := j.PublicKey(); err == nil && j.Kid != "" {
			keys[j.Kid] = pk
		}
	}
	p.keys, p.keysAt = keys, now
	if k, ok := keys[kid]; ok {
		return k, nil
	}
	return nil, errors.New("oidc: unknown signing key")
}

// AuthURL builds the provider's authorization request. The nonce binds the
// ID token to this login state; PKCE is sent too because Entra supports it
// and it costs nothing.
func (p *OIDCProvider) AuthURL(ctx context.Context, redirectURI, state, nonce, pkceVerifier string) (string, error) {
	d, err := p.discover(ctx)
	if err != nil {
		return "", err
	}
	sum := sha256.Sum256([]byte(pkceVerifier))
	q := url.Values{
		"client_id":             {p.ClientID},
		"response_type":         {"code"},
		"redirect_uri":          {redirectURI},
		"response_mode":         {"query"},
		"scope":                 {"openid profile email"},
		"state":                 {state},
		"nonce":                 {nonce},
		"code_challenge":        {base64.RawURLEncoding.EncodeToString(sum[:])},
		"code_challenge_method": {"S256"},
		// Always let the user pick which Microsoft account. Without this, the
		// provider silently reuses whatever account is active in the browser,
		// so a user signed in to both a work and a personal account binds the
		// wrong identity with no chance to choose — and because the hub keys
		// routing by user_id, an MCP session and a pane that resolved to
		// different accounts never meet (seen live 2026-09-08).
		"prompt": {"select_account"},
	}
	return d.AuthorizationEndpoint + "?" + q.Encode(), nil
}

// Exchange redeems the provider's code and validates the ID token (OIDC
// Core §3.1.3.7): signature against the provider's JWKS, issuer (per
// tenant for the `common` authority), audience, nonce, expiry.
func (p *OIDCProvider) Exchange(ctx context.Context, code, redirectURI, pkceVerifier, nonce string) (Identity, error) {
	d, err := p.discover(ctx)
	if err != nil {
		return Identity{}, err
	}
	form := url.Values{
		"client_id":     {p.ClientID},
		"client_secret": {p.ClientSecret},
		"grant_type":    {"authorization_code"},
		"code":          {code},
		"redirect_uri":  {redirectURI},
		"code_verifier": {pkceVerifier},
	}
	ctx, cancel := context.WithTimeout(ctx, providerTimeout)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, d.TokenEndpoint, strings.NewReader(form.Encode()))
	if err != nil {
		return Identity{}, err
	}
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	resp, err := p.client().Do(req)
	if err != nil {
		return Identity{}, fmt.Errorf("oidc token request: %w", err)
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		return Identity{}, err
	}
	var tok struct {
		IDToken   string `json:"id_token"`
		Error     string `json:"error"`
		ErrorDesc string `json:"error_description"`
	}
	if err := json.Unmarshal(body, &tok); err != nil || resp.StatusCode != http.StatusOK || tok.IDToken == "" {
		if tok.Error != "" {
			// Entra's descriptions are long but useful and carry no secret.
			return Identity{}, fmt.Errorf("oidc token endpoint: %s: %s", tok.Error, tok.ErrorDesc)
		}
		return Identity{}, fmt.Errorf("oidc token endpoint: HTTP %d without id_token", resp.StatusCode)
	}
	payload, err := auth.VerifyJWS(tok.IDToken, func(kid, alg string) (crypto.PublicKey, error) {
		return p.key(ctx, d.JWKSURI, kid)
	})
	if err != nil {
		return Identity{}, fmt.Errorf("id token: %w", err)
	}
	var c struct {
		Issuer   string        `json:"iss"`
		Subject  string        `json:"sub"`
		Audience auth.Audience `json:"aud"`
		Expires  int64         `json:"exp"`
		IssuedAt int64         `json:"iat"`
		Nonce    string        `json:"nonce"`
		OID      string        `json:"oid"`
		TID      string        `json:"tid"`
		Email    string        `json:"email"`
		Username string        `json:"preferred_username"`
		Name     string        `json:"name"`
	}
	if err := json.Unmarshal(payload, &c); err != nil {
		return Identity{}, errors.New("id token: claims are not JSON")
	}
	now := p.now()
	switch {
	case !c.Audience.Contains(p.ClientID):
		return Identity{}, errors.New("id token: audience is not this application")
	case c.Nonce == "" || c.Nonce != nonce:
		return Identity{}, errors.New("id token: nonce mismatch")
	case c.Expires == 0 || now.After(time.Unix(c.Expires, 0).Add(time.Minute)):
		return Identity{}, errors.New("id token: expired")
	case c.Issuer != expectedIssuer(d.Issuer, c.TID):
		return Identity{}, fmt.Errorf("id token: issuer %q is not the tenant's", c.Issuer)
	}
	// Entra: `oid` is the stable object id across the tenant's apps; `sub`
	// is per-application. Either is stable for us, oid is the documented
	// one. Other providers only have sub.
	subject := c.OID
	if subject == "" {
		subject = c.Subject
	}
	if subject == "" {
		return Identity{}, errors.New("id token: no subject")
	}
	email := c.Email
	if email == "" {
		email = c.Username
	}
	return Identity{Subject: subject, Tenant: c.TID, Email: email, DisplayName: c.Name}, nil
}

// expectedIssuer resolves Entra's templated issuer
// ("https://login.microsoftonline.com/{tenantid}/v2.0") for the token's
// tenant; a provider with a literal issuer is compared as is.
func expectedIssuer(template, tid string) string {
	if strings.Contains(template, "{tenantid}") {
		if tid == "" {
			return "" // never matches: a templated issuer needs a tenant
		}
		return strings.ReplaceAll(template, "{tenantid}", tid)
	}
	return template
}
