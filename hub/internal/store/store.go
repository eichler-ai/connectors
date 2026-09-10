// Package store persists the authorization server's records (PRD §11):
// users, OAuth clients, authorization codes, refresh tokens, the panes'
// bridge tokens and the in-flight login state. Two implementations: Memory
// for -dev and tests, Firestore for
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
// parameters validated until the consent decision — or one in-flight pane
// sign-in (Return set, no client), which ends at the bridge-token page
// instead of consent. Keyed by a random id that travels through the sign-in
// redirects as the provider's `state`.
type LoginState struct {
	ID string `firestore:"id"`
	// Return is a hub-local path the login callback sends the user to
	// instead of the consent page; set only by the pane's mint flow
	// (/bridge/authorize), and then the OAuth fields below are empty.
	Return        string    `firestore:"return,omitempty"`
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

// BridgeToken is the pane's long-lived credential (§06 path 1, §18.8):
// minted when the user signs in inside the extension, presented in every
// WebSocket hello, 90 days, revocable. Stored by hash like a refresh token.
// Revocation deletes the record — there is no rotation or reuse detection
// to preserve, and a deleted token reads back as ErrNotFound, which is all
// the verifier needs.
type BridgeToken struct {
	Hash      string `firestore:"hash"`
	UserID    string `firestore:"user_id"`
	Connector string `firestore:"connector"`
	// Label is what the extension said about itself at mint time (host and
	// platform), for a future "your connected apps" list; free text, bounded.
	Label      string    `firestore:"label,omitempty"`
	CreatedAt  time.Time `firestore:"created_at"`
	ExpiresAt  time.Time `firestore:"expires_at"`
	LastUsedAt time.Time `firestore:"last_used_at"`
}

// AuditRow is one exec/export/import outcome (PRD §11 "audit", §12, §13's
// compensating control for arbitrary code execution). It never carries a
// token, a script *result*, or file bytes: identities, a script hash, a
// bounded copy of the script text (exec only, so a reviewer can see what ran
// without an unbounded blob), the outcome, and sizes. ExpiresAt is set for
// bounded retention; deploy.sh puts a Firestore TTL policy on it.
type AuditRow struct {
	ID        string    `firestore:"id"`
	Timestamp time.Time `firestore:"timestamp"`
	UserID    string    `firestore:"user_id"`
	Connector string    `firestore:"connector"`
	Instance  string    `firestore:"instance_id"`
	Document  string    `firestore:"document_id,omitempty"`
	// Action is "exec", "export", "import" or "create".
	Action string `firestore:"action"`
	// ScriptSHA256 and ScriptBounded are exec only.
	ScriptSHA256  string `firestore:"script_sha256,omitempty"`
	ScriptBounded string `firestore:"script_bounded,omitempty"`
	Language      string `firestore:"language,omitempty"`
	OK            bool   `firestore:"ok"`
	// Code is the outcome's stable diagnostic code, empty on success.
	Code       string `firestore:"code,omitempty"`
	DurationMs int64  `firestore:"duration_ms"`
	// ResultBytes is exec's result size; FileBytes is export/import/create's
	// file size; Format is export/import/create's file format. Never the bytes.
	ResultBytes int    `firestore:"result_bytes,omitempty"`
	FileBytes   int64  `firestore:"file_bytes,omitempty"`
	Format      string `firestore:"format,omitempty"`
	// Name and Sheets are create only: the OneDrive file name the workbook was
	// created under (provenance — RFC §5 wants the broad Graph scope's writes
	// named) and how many sheets it holds. Never the sheet names or cell
	// values, matching import's "count, never names" rule.
	Name   string `firestore:"name,omitempty"`
	Sheets int    `firestore:"sheets,omitempty"`
	// Client is the MCP client's Implementation.Name when the SDK session
	// exposes it; empty when it does not (see hub.clientNameOf).
	Client    string    `firestore:"client,omitempty"`
	ExpiresAt time.Time `firestore:"expires_at"`
}

// GraphToken is the user's Microsoft refresh token for Graph calls (RFC
// excel/docs/rfc-graph-create-and-open.md §3.1), one per user, keyed by
// user id. Ciphertext is AES-256-GCM over the token with a key derived from
// KeySet.Secret("graph-token-enc") (hub/internal/graphtoken) — the store
// only ever holds and returns opaque bytes, never the plaintext token, so
// it cannot leak it into a log by accident.
type GraphToken struct {
	UserID     string    `firestore:"user_id"`
	Ciphertext []byte    `firestore:"ciphertext"`
	UpdatedAt  time.Time `firestore:"updated_at"`
}

// Store is what the authorization server needs from persistence.
type Store interface {
	// UserByIdentity finds the user for a provider subject, or ErrNotFound.
	UserByIdentity(ctx context.Context, provider, subject string) (User, error)
	// User finds a user by the hub's own id, or ErrNotFound.
	User(ctx context.Context, id string) (User, error)
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
	// RevokeUser revokes every refresh token, deletes every bridge token, and
	// deletes the Graph token of a user (the §13 kill switch — the Graph
	// grant is file access to the user's whole OneDrive, so the kill switch
	// must cover it too; outstanding access tokens expire on their own
	// within 15 minutes, and a pane's socket ends at its next reconnect).
	// Returns how many records of all three kinds it touched.
	RevokeUser(ctx context.Context, userID string) (int, error)

	// PutGraphToken stores (or overwrites) user's encrypted Graph refresh
	// token; called on every sign-in that grants Files access, and again
	// whenever the Graph client rotates the token.
	PutGraphToken(ctx context.Context, t GraphToken) error
	// GraphToken looks up user's stored token, or ErrNotFound if they never
	// granted Files access (or RevokeUser deleted it).
	GraphToken(ctx context.Context, userID string) (GraphToken, error)
	// DeleteGraphToken removes user's stored token; deleting an unknown user
	// is not an error (matches RevokeBridgeToken's contract).
	DeleteGraphToken(ctx context.Context, userID string) error

	PutBridgeToken(ctx context.Context, t BridgeToken) error
	// BridgeToken looks a token up by hash; unknown, revoked and expired
	// all read as ErrNotFound.
	BridgeToken(ctx context.Context, hash string) (BridgeToken, error)
	// TouchBridgeToken records a successful hello. Best effort by contract:
	// callers log its error and carry on.
	TouchBridgeToken(ctx context.Context, hash string, at time.Time) error
	// RevokeBridgeToken deletes the token; revoking an unknown hash is not
	// an error (the pane's sign-out must not fail on a token already gone).
	RevokeBridgeToken(ctx context.Context, hash string) error

	// PutAuditRow appends one audit row (§11 "per user": audit/{user_id}/rows
	// in Firestore, so a RevokeUser-style delete can be scoped to one user).
	PutAuditRow(ctx context.Context, row AuditRow) error
	// RecentAudit returns up to limit rows for user, newest first. Cheap to
	// add now; a get_audit tool is a later item, not built here.
	RecentAudit(ctx context.Context, userID string, limit int) ([]AuditRow, error)

	Close() error
}
