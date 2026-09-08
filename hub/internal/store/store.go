// Package store persists the authorization server's records (PRD §11):
// users, OAuth clients, authorization codes, refresh tokens and the in-flight
// login state. Two implementations: Memory for -dev and tests, Firestore for
// the deployment. The interface is deliberately shaped around the protocol's
// atomic steps (consume a code, rotate a refresh token) rather than generic
// get/put, so the reuse-detection guarantees live in one place per backend.
package store

import (
	"context"
	"errors"
	"time"
)

var (
	// ErrNotFound: no such record (or it expired).
	ErrNotFound = errors.New("store: not found")
	// ErrReused: the code or refresh token was presented a second time. The
	// store has already revoked what it can (the whole refresh-token family,
	// OAuth 2.1 §4.3.1); the caller only has to refuse the request.
	ErrReused = errors.New("store: token reused")
	// ErrRevoked: the refresh token's family was revoked earlier.
	ErrRevoked = errors.New("store: token revoked")
)

// User is the identity record (§06). ID is the hub's own stable id; the
// provider pair is the lookup key on login.
type User struct {
	ID          string    `firestore:"id"`
	Provider    string    `firestore:"provider"`
	Subject     string    `firestore:"subject"`
	Tenant      string    `firestore:"tenant,omitempty"`
	Email       string    `firestore:"email,omitempty"`
	DisplayName string    `firestore:"display_name,omitempty"`
	CreatedAt   time.Time `firestore:"created_at"`
	LastLoginAt time.Time `firestore:"last_login_at"`
}

// Client is a dynamically registered OAuth client (RFC 7591). Clients that
// arrive by Client ID Metadata Document are not stored — their document is
// the registration — so this is DCR only.
type Client struct {
	ID           string    `firestore:"id"`
	Name         string    `firestore:"name,omitempty"`
	RedirectURIs []string  `firestore:"redirect_uris"`
	CreatedAt    time.Time `firestore:"created_at"`
	// LastUsedAt is zero until the client completes an authorization; the
	// collector removes registrations that stay zero past the grace period.
	LastUsedAt time.Time `firestore:"last_used_at"`
}

// LoginState is one in-flight /oauth/authorize request, from the moment the
// parameters validated until the consent decision. Keyed by a random id that
// travels through the sign-in redirects as the provider's `state`.
type LoginState struct {
	ID            string    `firestore:"id"`
	ClientID      string    `firestore:"client_id"`
	ClientName    string    `firestore:"client_name"`
	RedirectURI   string    `firestore:"redirect_uri"`
	State         string    `firestore:"state"`
	CodeChallenge string    `firestore:"code_challenge"`
	Scope         []string  `firestore:"scope"`
	Resource      string    `firestore:"resource"`
	Nonce         string    `firestore:"nonce,omitempty"`
	ProviderPKCE  string    `firestore:"provider_pkce,omitempty"`
	ExpiresAt     time.Time `firestore:"expires_at"`
}

// AuthCode is an issued authorization code, stored by hash. ID doubles as
// the refresh-token family id for everything minted from this code, so a
// replayed code revokes the family (OAuth 2.1 §4.1.2).
type AuthCode struct {
	Hash          string    `firestore:"hash"`
	ID            string    `firestore:"id"`
	ClientID      string    `firestore:"client_id"`
	RedirectURI   string    `firestore:"redirect_uri"`
	CodeChallenge string    `firestore:"code_challenge"`
	UserID        string    `firestore:"user_id"`
	Scope         []string  `firestore:"scope"`
	Resource      string    `firestore:"resource"`
	ExpiresAt     time.Time `firestore:"expires_at"`
	UsedAt        time.Time `firestore:"used_at"`
}

// RefreshToken is stored by hash; the token itself is only ever in the
// client's hands.
type RefreshToken struct {
	Hash      string    `firestore:"hash"`
	FamilyID  string    `firestore:"family_id"`
	UserID    string    `firestore:"user_id"`
	ClientID  string    `firestore:"client_id"`
	Scope     []string  `firestore:"scope"`
	Resource  string    `firestore:"resource"`
	CreatedAt time.Time `firestore:"created_at"`
	ExpiresAt time.Time `firestore:"expires_at"`
	UsedAt    time.Time `firestore:"used_at"`
	Revoked   bool      `firestore:"revoked"`
}

// Store is what the authorization server needs from persistence.
type Store interface {
	// UserByIdentity finds the user for a provider subject, or ErrNotFound.
	UserByIdentity(ctx context.Context, provider, subject string) (User, error)
	// PutUser creates or replaces the user and its identity lookup entry.
	PutUser(ctx context.Context, u User) error

	PutClient(ctx context.Context, c Client) error
	Client(ctx context.Context, id string) (Client, error)
	// TouchClient records a completed authorization.
	TouchClient(ctx context.Context, id string, at time.Time) error
	// DeleteUnusedClients removes registrations created before cutoff that
	// never completed an authorization; returns how many.
	DeleteUnusedClients(ctx context.Context, cutoff time.Time) (int, error)

	PutLoginState(ctx context.Context, s LoginState) error
	LoginState(ctx context.Context, id string) (LoginState, error)
	DeleteLoginState(ctx context.Context, id string) error

	PutAuthCode(ctx context.Context, c AuthCode) error
	// ConsumeAuthCode marks the code used and returns it. A second call for
	// the same hash returns ErrReused after revoking the code's family.
	ConsumeAuthCode(ctx context.Context, hash string, now time.Time) (AuthCode, error)

	PutRefreshToken(ctx context.Context, t RefreshToken) error
	// RotateRefreshToken atomically marks oldHash used and stores next with
	// the old record's bindings (family, user, client, scope, resource)
	// copied in; the caller supplies only next's hash and lifetime. It
	// returns the old record so the caller can check the bindings.
	// ErrReused means oldHash was already rotated: the family is revoked.
	RotateRefreshToken(ctx context.Context, oldHash string, now time.Time, next RefreshToken) (RefreshToken, error)
	// RevokeFamily revokes every refresh token minted from one code.
	RevokeFamily(ctx context.Context, familyID string) error
	// RevokeUser revokes every refresh token of a user (the §13 kill switch;
	// outstanding access tokens expire on their own within 15 minutes).
	RevokeUser(ctx context.Context, userID string) (int, error)

	Close() error
}
