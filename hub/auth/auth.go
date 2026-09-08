// Package auth is the seam between "who is this" and everything else. The
// bridge handler and the MCP endpoints only ever see an Authenticator and a
// Principal; phase 1's OAuth authorization server (PRD §06) replaces the
// implementation here without either of them changing. It is exported (not
// internal/) only so a connector module's tests can build a hub.
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
	// Expires is when the presented token stops being valid, or zero for a
	// token whose lifetime is managed elsewhere (the dev token).
	Expires time.Time
}

// ErrInvalidToken is returned for any token that does not verify. Callers
// must not distinguish "unknown" from "expired" from "wrong" to the client.
var ErrInvalidToken = errors.New("invalid token")

// Authenticator verifies the two kinds of token the hub sees. They are two
// methods rather than one because phase 1 gives them different formats and
// lifetimes (15-minute access JWTs on /mcp, 90-day bridge tokens in hello —
// §06, §18.8); the dev stand-in happens to accept the same value for both.
type Authenticator interface {
	// VerifyAccessToken checks a bearer token presented on /<connector>/mcp.
	VerifyAccessToken(ctx context.Context, token string) (Principal, error)
	// VerifyBridgeToken checks the token field of a bridge hello.
	VerifyBridgeToken(ctx context.Context, token string) (Principal, error)
}

// DevToken is the phase-0 stand-in: one shared secret from HUB_DEV_TOKEN,
// one fixed user derived from it. It exists so the end-to-end path can be
// exercised before the authorization server exists and is not a production
// mode: the hub refuses to start without a token, and the token is never
// written to a log.
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

func (d *DevToken) verify(token string) (Principal, error) {
	if subtle.ConstantTimeCompare([]byte(token), []byte(d.token)) != 1 {
		return Principal{}, ErrInvalidToken
	}
	return Principal{UserID: d.userID}, nil
}

func (d *DevToken) VerifyAccessToken(_ context.Context, token string) (Principal, error) {
	return d.verify(token)
}

func (d *DevToken) VerifyBridgeToken(_ context.Context, token string) (Principal, error) {
	return d.verify(token)
}

// RequireBearer wraps an MCP endpoint with the go-sdk's bearer middleware,
// backed by a. The SDK then binds each MCP session to the UserID it was
// created with, which is what stops one user's session id from being replayed
// by another (§13).
func RequireBearer(a Authenticator) func(http.Handler) http.Handler {
	verifier := func(ctx context.Context, token string, _ *http.Request) (*mcpauth.TokenInfo, error) {
		p, err := a.VerifyAccessToken(ctx, token)
		if err != nil {
			// The SDK maps anything unwrapping to its ErrInvalidToken to a
			// 401 with no detail, which is the behaviour we want.
			return nil, mcpauth.ErrInvalidToken
		}
		return &mcpauth.TokenInfo{UserID: p.UserID, Expiration: p.Expires}, nil
	}
	return mcpauth.RequireBearerToken(verifier, &mcpauth.RequireBearerTokenOptions{
		// The dev token has no expiry of its own. Phase 1's JWTs carry one,
		// and this flag should go back to false with them.
		AllowMissingExpiration: true,
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
