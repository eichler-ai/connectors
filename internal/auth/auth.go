// Package auth is the seam between "who is this" and everything else. The
// bridge handler and the MCP endpoints only ever see an Authenticator and a
// Principal. Phase 1 fills the access-token half with JWTs minted by the
// hub's own authorization server (PRD §06; JWT here, the server in
// hub/internal/authserver); the bridge-token half is still the phase-0 dev
// token until pane sign-in lands (unit 2).
//
// It sits at the repository's internal/ rather than hub/internal/ because a
// connector package's tests build a hub with the dev token, and Go's internal
// rule would keep hub/internal/auth out of excel/.
package auth

import (
	"context"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"errors"
	"net/http"
	"time"

	mcpauth "github.com/modelcontextprotocol/go-sdk/auth"
)

// Principal is the verified identity behind a token.
type Principal struct {
	// UserID is the routing key: an exec from an MCP session for this user
	// only ever reaches a bridge that presented a token for the same UserID
	// (§13). No other field of the identity record is needed by the hub core.
	UserID string
	// Scopes are the connector slugs the token grants (§18.3: one audience,
	// per-connector scopes). Empty for a bridge token, which is scoped by
	// the socket it arrives on.
	Scopes []string
	// Expires is when the presented token stops being valid, or zero for a
	// token whose lifetime is managed elsewhere (the dev token).
	Expires time.Time
}

// ErrInvalidToken is returned for any token that does not verify. Callers
// must not distinguish "unknown" from "expired" from "wrong" to the client.
var ErrInvalidToken = errors.New("invalid token")

// Authenticator verifies the two kinds of token the hub sees. They are two
// methods rather than one because they have different formats and
// lifetimes (15-minute access JWTs on /mcp, 90-day bridge tokens in hello —
// §06, §18.8).
type Authenticator interface {
	// VerifyAccessToken checks a bearer token presented on /<connector>/mcp.
	VerifyAccessToken(ctx context.Context, token string) (Principal, error)
	// VerifyBridgeToken checks the token field of a bridge hello.
	VerifyBridgeToken(ctx context.Context, token string) (Principal, error)
}

// Split routes each token kind to its own verifier, so the JWT verifier
// (access) and the dev token (bridge, until unit 2) compose into the one
// Authenticator the hub takes.
type Split struct {
	Access Authenticator
	Bridge Authenticator
}

func (s Split) VerifyAccessToken(ctx context.Context, token string) (Principal, error) {
	return s.Access.VerifyAccessToken(ctx, token)
}

func (s Split) VerifyBridgeToken(ctx context.Context, token string) (Principal, error) {
	return s.Bridge.VerifyBridgeToken(ctx, token)
}

// DevToken is the phase-0 stand-in: one shared secret from HUB_DEV_TOKEN,
// one fixed user derived from it. Since phase 1 it verifies bridge hellos
// only — the MCP endpoints take the authorization server's JWTs and nothing
// else — and it goes away entirely when the pane signs in (unit 2). It is
// not a production identity: the token is never written to a log.
type DevToken struct {
	token  string
	userID string
}

// NewDevToken builds the stand-in. The user id is a hash prefix of the token
// so that two hubs run with different tokens do not share a user id by
// accident, while the same token always maps to the same user.
func NewDevToken(token string) (*DevToken, error) {
	if len(token) < 16 {
		return nil, errors.New("HUB_DEV_TOKEN must be at least 16 characters")
	}
	sum := sha256.Sum256([]byte(token))
	return &DevToken{token: token, userID: "dev-" + hex.EncodeToString(sum[:4])}, nil
}

// UserID is the fixed identity every bridge presenting the dev token gets.
func (d *DevToken) UserID() string { return d.userID }

// VerifyAccessToken always fails: the dev token was retired from /mcp when
// the authorization server arrived (phase 1). It stays on the type so a
// DevToken alone is still an Authenticator for tests of the bridge side.
func (d *DevToken) VerifyAccessToken(context.Context, string) (Principal, error) {
	return Principal{}, ErrInvalidToken
}

func (d *DevToken) VerifyBridgeToken(_ context.Context, token string) (Principal, error) {
	if subtle.ConstantTimeCompare([]byte(token), []byte(d.token)) != 1 {
		return Principal{}, ErrInvalidToken
	}
	return Principal{UserID: d.userID}, nil
}

// BearerOptions configure RequireBearer for one MCP endpoint.
type BearerOptions struct {
	// ResourceMetadataURL is advertised in the WWW-Authenticate header of
	// every 401/403 so a Claude client can find the authorization server
	// (RFC 9728 §5.1, MCP authorization spec).
	ResourceMetadataURL string
	// Scopes the token must carry; a token without them gets 403 with the
	// same header plus the scope list (the MCP spec's step-up hint).
	Scopes []string
}

// RequireBearer wraps an MCP endpoint with the go-sdk's bearer middleware,
// backed by a. The SDK then binds each MCP session to the UserID it was
// created with, which is what stops one user's session id from being replayed
// by another (§13).
func RequireBearer(a Authenticator, opts BearerOptions) func(http.Handler) http.Handler {
	verifier := func(ctx context.Context, token string, _ *http.Request) (*mcpauth.TokenInfo, error) {
		p, err := a.VerifyAccessToken(ctx, token)
		if err != nil {
			// The SDK maps anything unwrapping to its ErrInvalidToken to a
			// 401 with no detail, which is the behaviour we want.
			return nil, mcpauth.ErrInvalidToken
		}
		return &mcpauth.TokenInfo{UserID: p.UserID, Scopes: p.Scopes, Expiration: p.Expires}, nil
	}
	return mcpauth.RequireBearerToken(verifier, &mcpauth.RequireBearerTokenOptions{
		ResourceMetadataURL: opts.ResourceMetadataURL,
		Scopes:              opts.Scopes,
		// Every access token is a JWT with exp; a verifier that yields none
		// is a bug, and the SDK's 401 is the right answer to it.
		AllowMissingExpiration: false,
		// The token endpoint and the resource server are the same process,
		// but a client that received a token at exp-0.5s should not lose it
		// to network latency.
		ClockSkew: 5 * time.Second,
	})
}

// UserID extracts the authenticated user from a request context the
// middleware has already passed, or "" when there is none.
func UserID(ctx context.Context) string {
	if ti := mcpauth.TokenInfoFromContext(ctx); ti != nil {
		return ti.UserID
	}
	return ""
}
