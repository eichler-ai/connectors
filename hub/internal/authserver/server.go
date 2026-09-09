// Package authserver is the hub's OAuth 2.1 authorization server (PRD §05,
// §06, §13): metadata, /oauth/authorize with PKCE, /oauth/token with
// refresh rotation and reuse detection, dynamic client registration and
// Client ID Metadata Documents, sign-in delegated to Microsoft, and a
// consent page. Hand-rolled against the RFCs rather than ory/fosite — the
// PR that introduced it records why — so every rule the §13 checklist names
// is a few lines here with a test beside it.
//
// It issues ES256 JWT access tokens for one audience, the hub itself, with
// connector slugs as scopes (§18.3); internal/auth verifies them on the MCP
// side from the same key set.
package authserver

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"html/template"
	"log/slog"
	"net"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"

	"golang.org/x/time/rate"

	"github.com/eichler-ai/connectors/hub/internal/store"
	"github.com/eichler-ai/connectors/internal/auth"
)

// Options configure the server.
type Options struct {
	// Issuer is the hub's public origin, e.g. https://connectors.eichler.ai:
	// the `iss` of every token, the audience of every access token, and the
	// base of every path below.
	Issuer string
	Store  store.Store
	Keys   *auth.KeySet
	// Connectors are the slugs that double as scopes (§18.3).
	Connectors []string
	// Provider handles sign-in. Nil is allowed only in tests that never
	// reach /login.
	Provider *OIDCProvider
	Logger   *slog.Logger
	// HTTPClient fetches Client ID Metadata Documents; tests inject one
	// that trusts their TLS server. Nil builds the SSRF-guarded default.
	HTTPClient *http.Client
	// AllowLoopbackCIMD lets a client_id URL resolve to a loopback address,
	// for tests and -dev. Production must leave it false (SSRF, CIMD §6.5).
	AllowLoopbackCIMD bool
	// TrustForwardedFor reads the client IP for rate limiting from
	// X-Forwarded-For (Cloud Run sets it and nothing else reaches the
	// container); off in -dev where the header is attacker-controlled.
	TrustForwardedFor bool
	// Now is overridable for expiry tests.
	Now func() time.Time
}

// Lifetimes. Access: PRD §06. Refresh: 30 days, sliding (each rotation
// issues a fresh 30 days); a client idle for a month signs in again. Bridge:
// 90 days fixed (§18.8) — the pane signs in again, there is no rotation.
// Codes and login states are single-shot and short.
const (
	refreshTokenTTL   = 30 * 24 * time.Hour
	bridgeTokenTTL    = 90 * 24 * time.Hour
	authCodeTTL       = 5 * time.Minute
	loginStateTTL     = 10 * time.Minute
	sessionTTL        = time.Hour
	registrationGrace = 24 * time.Hour
	// scopeOffline is accepted for clients that request it and otherwise
	// ignored: every authorization gets a refresh token, because every
	// Claude client is a long-lived installation that needs one.
	scopeOffline = "offline_access"
)

// Server serves the authorization server routes.
type Server struct {
	o        Options
	log      *slog.Logger
	verifier *auth.JWTVerifier
	cimd     *cimdCache
	reg      *ipLimiter
	tmpl     *template.Template
	now      func() time.Time
}

// New validates the options and builds the server.
func New(o Options) (*Server, error) {
	if o.Store == nil || o.Keys == nil {
		return nil, errors.New("authserver: Store and Keys are required")
	}
	u, err := url.Parse(o.Issuer)
	if err != nil || u.Scheme == "" || u.Host == "" || u.Path != "" && u.Path != "/" {
		return nil, fmt.Errorf("authserver: Issuer %q must be an origin with no path", o.Issuer)
	}
	o.Issuer = strings.TrimSuffix(o.Issuer, "/")
	if len(o.Connectors) == 0 {
		return nil, errors.New("authserver: at least one connector slug is required")
	}
	if o.Logger == nil {
		o.Logger = slog.Default()
	}
	now := o.Now
	if now == nil {
		now = time.Now
	}
	s := &Server{
		o:    o,
		log:  o.Logger,
		now:  now,
		tmpl: template.Must(template.New("pages").Parse(pages)),
		reg:  newIPLimiter(rate.Every(time.Minute), 10),
	}
	s.verifier = &auth.JWTVerifier{Keys: o.Keys, Issuer: o.Issuer, Audience: o.Issuer, Now: now}
	s.cimd = newCIMDCache(o.HTTPClient, o.AllowLoopbackCIMD, now)
	return s, nil
}

// Verifier is the resource-server side for the tokens this server mints.
func (s *Server) Verifier() auth.Authenticator { return s.verifier }

// Issuer is the normalised issuer URL.
func (s *Server) Issuer() string { return s.o.Issuer }

// Routes mounts every path on mux (PRD §05).
func (s *Server) Routes(mux *http.ServeMux) {
	mux.HandleFunc("GET /.well-known/oauth-authorization-server", s.metadata)
	// RFC 8414 is the primary document; the MCP spec has clients try the
	// OpenID Connect path second and Claude clients have been seen to
	// start there, so the same document answers both. code_challenge_
	// methods_supported is present either way, as the spec demands.
	mux.HandleFunc("GET /.well-known/openid-configuration", s.metadata)
	mux.HandleFunc("GET /oauth/jwks", s.jwks)
	mux.HandleFunc("GET /oauth/authorize", s.authorize)
	mux.HandleFunc("GET /oauth/consent", s.consentGet)
	mux.HandleFunc("POST /oauth/consent", s.consentPost)
	mux.HandleFunc("POST /oauth/token", s.token)
	mux.HandleFunc("POST /oauth/register", s.register)
	mux.HandleFunc("GET /login/microsoft", s.loginStart)
	mux.HandleFunc("GET /login/microsoft/callback", s.loginCallback)
	mux.HandleFunc("GET /login/switch", s.loginSwitch)
	// Pane sign-in (PRD §06 path 1): not OAuth, but it shares the login
	// and the session, so it lives here. See bridge.go.
	mux.HandleFunc("GET /bridge/authorize", s.bridgeAuthorize)
	mux.HandleFunc("POST /bridge/revoke", s.bridgeRevoke)
}

// RunCollector garbage-collects client registrations that never completed
// an authorization (§13), every hour until ctx ends.
func (s *Server) RunCollector(ctx context.Context) {
	t := time.NewTicker(time.Hour)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			s.collectOnce(ctx)
		}
	}
}

func (s *Server) collectOnce(ctx context.Context) {
	n, err := s.o.Store.DeleteUnusedClients(ctx, s.now().Add(-registrationGrace))
	if err != nil {
		s.log.Warn("authserver: client collection failed", "err", err)
		return
	}
	if n > 0 {
		s.log.Info("authserver: collected unused client registrations", "count", n)
	}
}

// metadata serves RFC 8414 (and the OIDC alias).
func (s *Server) metadata(w http.ResponseWriter, r *http.Request) {
	iss := s.o.Issuer
	doc := map[string]any{
		"issuer":                                         iss,
		"authorization_endpoint":                         iss + "/oauth/authorize",
		"token_endpoint":                                 iss + "/oauth/token",
		"registration_endpoint":                          iss + "/oauth/register",
		"jwks_uri":                                       iss + "/oauth/jwks",
		"scopes_supported":                               append(append([]string{}, s.o.Connectors...), scopeOffline),
		"response_types_supported":                       []string{"code"},
		"response_modes_supported":                       []string{"query"},
		"grant_types_supported":                          []string{"authorization_code", "refresh_token"},
		"token_endpoint_auth_methods_supported":          []string{"none"},
		"code_challenge_methods_supported":               []string{"S256"},
		"client_id_metadata_document_supported":          true,
		"authorization_response_iss_parameter_supported": true,
		"service_documentation":                          "https://github.com/eichler-ai/connectors/blob/main/hub/README.md",
	}
	publicJSON(w, http.StatusOK, doc)
}

func (s *Server) jwks(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Access-Control-Allow-Origin", "*")
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "public, max-age=300")
	_, _ = w.Write(s.o.Keys.JWKS())
}

// publicJSON writes a discovery document: world-readable, cacheable,
// cross-origin (RFC 9728 §3.1 reasoning — nothing in it is secret).
func publicJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Access-Control-Allow-Origin", "*")
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "public, max-age=300")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(v)
}

// oauthError writes an RFC 6749 §5.2 error body. Never cached, never
// cross-origin: the token endpoint is not a public document.
func oauthError(w http.ResponseWriter, code int, err, desc string) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(map[string]string{"error": err, "error_description": desc})
}

// acceptedResource reports whether a `resource` parameter (RFC 8707) names
// this hub: the issuer itself, or one connector's MCP endpoint under it.
// Either way the token's audience is the hub (§18.3); a connector endpoint
// additionally becomes the default scope when the client sends none. The
// endpoint form is accepted because every MCP client sends the URL it was
// configured with, and the go-sdk client will only trust protected-resource
// metadata whose `resource` equals that URL exactly.
func (s *Server) acceptedResource(res string) (slug string, ok bool) {
	u, err := url.Parse(res)
	if err != nil || u.Scheme == "" || u.Host == "" || u.Fragment != "" || u.RawQuery != "" {
		return "", false
	}
	// The MCP spec asks servers to accept an upper-cased scheme or host;
	// paths stay case-sensitive.
	norm := strings.ToLower(u.Scheme) + "://" + strings.ToLower(u.Host) + strings.TrimSuffix(u.Path, "/")
	if norm == s.o.Issuer {
		return "", true
	}
	for _, c := range s.o.Connectors {
		if norm == s.o.Issuer+"/"+c+"/mcp" {
			return c, true
		}
	}
	return "", false
}

// resolveScope validates a requested scope list against the connector
// slugs and fills in the default: the connector named by the resource, or
// every connector when the resource is the hub root.
func (s *Server) resolveScope(requested []string, resourceSlug string) ([]string, error) {
	var out []string
	seen := map[string]bool{}
	for _, sc := range requested {
		if sc == scopeOffline || seen[sc] {
			continue
		}
		if !s.isConnector(sc) {
			return nil, fmt.Errorf("unknown scope %q", sc)
		}
		seen[sc] = true
		out = append(out, sc)
	}
	if len(out) == 0 {
		if resourceSlug != "" {
			return []string{resourceSlug}, nil
		}
		return append([]string{}, s.o.Connectors...), nil
	}
	return out, nil
}

func (s *Server) isConnector(slug string) bool {
	for _, c := range s.o.Connectors {
		if c == slug {
			return true
		}
	}
	return false
}

func subset(sub, of []string) bool {
	for _, a := range sub {
		found := false
		for _, b := range of {
			if a == b {
				found = true
				break
			}
		}
		if !found {
			return false
		}
	}
	return true
}

// randomToken returns 32 random bytes, base64url: codes, refresh tokens,
// state ids, user ids.
func randomToken() string {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		panic("crypto/rand unavailable: " + err.Error())
	}
	return base64.RawURLEncoding.EncodeToString(b)
}

// hashToken is how codes and refresh tokens are stored: the store holds
// only the digest, so a leaked database cannot mint anything.
func hashToken(tok string) string {
	sum := sha256.Sum256([]byte(tok))
	return hex.EncodeToString(sum[:])
}

// clientIP is the rate-limiting key.
func (s *Server) clientIP(r *http.Request) string {
	if s.o.TrustForwardedFor {
		if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
			// Cloud Run appends the connecting client; the first entry is
			// what it saw, later ones are whatever the client sent.
			parts := strings.Split(xff, ",")
			return strings.TrimSpace(parts[len(parts)-1])
		}
	}
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}

// ipLimiter is a per-IP token bucket. Bound: at most maxEntries IPs are
// tracked; on overflow the entries idle longest are dropped, and an entry
// idle for more than idleTTL is dropped whenever a new IP arrives.
type ipLimiter struct {
	mu      sync.Mutex
	rate    rate.Limit
	burst   int
	entries map[string]*ipEntry
}

type ipEntry struct {
	lim  *rate.Limiter
	seen time.Time
}

const (
	limiterMaxEntries = 10000
	limiterIdleTTL    = 10 * time.Minute
)

func newIPLimiter(r rate.Limit, burst int) *ipLimiter {
	return &ipLimiter{rate: r, burst: burst, entries: map[string]*ipEntry{}}
}

func (l *ipLimiter) allow(ip string, now time.Time) bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	e, ok := l.entries[ip]
	if !ok {
		if len(l.entries) >= limiterMaxEntries {
			for k, v := range l.entries {
				if now.Sub(v.seen) > limiterIdleTTL {
					delete(l.entries, k)
				}
			}
			for len(l.entries) >= limiterMaxEntries {
				var oldest string
				var oldestSeen time.Time
				for k, v := range l.entries {
					if oldest == "" || v.seen.Before(oldestSeen) {
						oldest, oldestSeen = k, v.seen
					}
				}
				delete(l.entries, oldest)
			}
		}
		e = &ipEntry{lim: rate.NewLimiter(l.rate, l.burst)}
		l.entries[ip] = e
	}
	e.seen = now
	return e.lim.AllowN(now, 1)
}
