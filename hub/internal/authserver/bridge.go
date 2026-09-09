package authserver

import (
	"context"
	"errors"
	"log/slog"
	"net/http"
	"net/url"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/eichler-ai/connectors/hub/internal/store"
	"github.com/eichler-ai/connectors/internal/auth"
)

// Pane sign-in (PRD §06 path 1). The extension opens /bridge/authorize in
// an Office dialog; the hub runs its ordinary Microsoft sign-in there,
// mints a 90-day bridge token bound to {session user, connector}, and hands
// it to the pane through Office.context.ui.messageParent (or a same-origin
// postMessage to window.opener for a plain popup). The pane then presents
// the token in every WebSocket hello, and the registry keys it under the
// same user_id an OAuth MCP session for that Microsoft account carries —
// which is what lets list_instances/execute_script from Claude find the
// pane (§07, §13).
//
// This is a new mint path, not an OAuth grant: nothing here touches
// /oauth/*, and the token is not a JWT. It is opaque, stored by hash, and
// looked up on every hello (BridgeVerifier), so revocation is immediate.
//
// TODO(pair): PRD §06 path 2, the pairing-code fallback for hosts where a
// sign-in popup is awkward, would mint through the same PutBridgeToken with
// the code's user; the hook is minting below, not the verifier.

// maxBridgeLabel bounds the free-text label the pane sends about itself.
const maxBridgeLabel = 80

// bridgePayload is what the page hands to the add-in. The field names are
// the pane's contract (excel/addin/taskpane.js).
type bridgePayload struct {
	Token     string `json:"token"`
	Connector string `json:"connector"`
	User      string `json:"user"`
	ExpiresAt string `json:"expires_at"`
}

// bridgeAuthorize is GET /bridge/authorize?connector=<slug>[&label=…]
// [&switch=1]. Signed out: straight to Microsoft, coming back here after.
// Signed in: mint and hand off — unless switch=1 (the pane's "Switch
// account"), which clears the session first so the request is treated as
// signed out and reaches the provider's chooser instead of the fast path.
// Minting on a GET is deliberate — the dialog's first navigation is the
// only request the pane makes — and safe because nobody but the Office
// host or a same-origin opener can receive the page's hand-off; a
// cross-site page that opens this URL only strands a token nobody holds,
// and the mint is logged either way.
func (s *Server) bridgeAuthorize(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	connector := q.Get("connector")
	if !s.isConnector(connector) {
		s.log.Info("bridge authorize: unknown connector", "connector", connector)
		s.errorPage(w, http.StatusBadRequest, "Unknown connector", "This page needs to be opened from a connector's add-in.")
		return
	}
	label := strings.TrimSpace(q.Get("label"))
	if !utf8.ValidString(label) {
		label = ""
	}
	if len(label) > maxBridgeLabel {
		label = label[:maxBridgeLabel]
	}
	// Rebuilt from the validated values, never echoed from the request, and
	// never carries switch/prompt: after the provider round trip the pane
	// lands back here signed in, and must take the fast path rather than
	// clearing the freshly set session again.
	returnURL := "/bridge/authorize?" + url.Values{"connector": {connector}, "label": {label}}.Encode()
	// ?switch=1 (or prompt=select_account, the OAuth-side spelling) is the
	// pane's "Switch account" button: force the provider round trip even
	// though a session exists, so its chooser fires instead of the fast
	// path silently reusing whoever is signed in (the bug this fixes).
	switchAccount := q.Get("switch") == "1" || q.Get("prompt") == "select_account"
	userID := s.sessionUser(r)
	if userID != "" && switchAccount {
		s.clearSession(w)
		userID = ""
	}
	if userID == "" {
		ls := store.LoginState{ID: randomToken(), Return: returnURL, ExpiresAt: s.now().Add(loginStateTTL)}
		s.startLogin(w, r, ls)
		return
	}
	user, err := s.o.Store.User(r.Context(), userID)
	if err != nil {
		// A session for a user the store no longer has (revoked and deleted,
		// or a -dev restart with an old cookie): sign in again.
		if errors.Is(err, store.ErrNotFound) {
			s.log.Info("bridge authorize: session user unknown; re-authenticating", "user", userID)
			s.clearSession(w)
			ls := store.LoginState{ID: randomToken(), Return: returnURL, ExpiresAt: s.now().Add(loginStateTTL)}
			s.startLogin(w, r, ls)
			return
		}
		s.log.Error("bridge authorize: load user", "err", err)
		s.errorPage(w, http.StatusInternalServerError, "Sign-in failed", "Your account could not be read.")
		return
	}
	now := s.now()
	tok := randomToken()
	bt := store.BridgeToken{
		Hash:      hashToken(tok),
		UserID:    userID,
		Connector: connector,
		Label:     label,
		CreatedAt: now,
		ExpiresAt: now.Add(bridgeTokenTTL),
	}
	if err := s.o.Store.PutBridgeToken(r.Context(), bt); err != nil {
		s.log.Error("bridge authorize: store token", "err", err)
		s.errorPage(w, http.StatusInternalServerError, "Sign-in failed", "The connection could not be recorded.")
		return
	}
	s.log.Info("bridge authorize: token minted", "user", userID, "connector", connector, "label", label, "expires_at", bt.ExpiresAt.Format(time.RFC3339))
	name := user.DisplayName
	if name == "" {
		name = user.Email
	}
	if name == "" {
		name = "you"
	}
	s.render(w, http.StatusOK, "bridge_token", pageData{
		Title:         "Signed in",
		ConnectorName: connector,
		UserName:      name,
		ScriptNonce:   randomToken()[:22],
		Payload:       bridgePayload{Token: tok, Connector: connector, User: name, ExpiresAt: bt.ExpiresAt.UTC().Format(time.RFC3339)},
	})
}

// bridgeRevoke is POST /bridge/revoke with form field `token`: the pane's
// sign-out. Holding the token is the authorization; the answer never says
// whether it existed (204 either way), like RFC 7009.
func (s *Server) bridgeRevoke(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Cache-Control", "no-store")
	if err := r.ParseForm(); err != nil {
		http.Error(w, "body must be application/x-www-form-urlencoded", http.StatusBadRequest)
		return
	}
	tok := r.PostForm.Get("token")
	if tok == "" {
		http.Error(w, "token is required", http.StatusBadRequest)
		return
	}
	hash := hashToken(tok)
	// The lookup is only for the log line; the delete does not need it.
	if bt, err := s.o.Store.BridgeToken(r.Context(), hash); err == nil {
		s.log.Info("bridge revoke: token revoked", "user", bt.UserID, "connector", bt.Connector)
	}
	if err := s.o.Store.RevokeBridgeToken(r.Context(), hash); err != nil {
		s.log.Error("bridge revoke: store", "err", err)
		http.Error(w, "store failure", http.StatusInternalServerError)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// BridgeVerifier is the hello-side check for the tokens bridgeAuthorize
// mints: hash, look up, refuse unknown/expired/revoked, touch last_used.
// Fallback, when set, is tried only after the store says "unknown"; cmd/hub
// wires HUB_DEV_TOKEN there for the transition, and -dev hubs and tests
// without a store rely on it.
type BridgeVerifier struct {
	store    store.Store
	fallback auth.Authenticator
	log      *slog.Logger
	now      func() time.Time
}

// NewBridgeVerifier builds the verifier; fallback and logger may be nil.
func NewBridgeVerifier(st store.Store, fallback auth.Authenticator, logger *slog.Logger) *BridgeVerifier {
	if logger == nil {
		logger = slog.Default()
	}
	return &BridgeVerifier{store: st, fallback: fallback, log: logger, now: time.Now}
}

// VerifyAccessToken always fails: bridge tokens are never bearer tokens.
func (v *BridgeVerifier) VerifyAccessToken(context.Context, string) (auth.Principal, error) {
	return auth.Principal{}, auth.ErrInvalidToken
}

func (v *BridgeVerifier) VerifyBridgeToken(ctx context.Context, token string) (auth.Principal, error) {
	if token == "" {
		return auth.Principal{}, auth.ErrInvalidToken
	}
	hash := hashToken(token)
	bt, err := v.store.BridgeToken(ctx, hash)
	switch {
	case err == nil:
		if bt.ExpiresAt.Before(v.now()) {
			// The store checks too; this guards a store whose clock lags.
			return auth.Principal{}, auth.ErrInvalidToken
		}
		if err := v.store.TouchBridgeToken(ctx, hash, v.now()); err != nil {
			v.log.Warn("bridge token: touch last_used_at", "user", bt.UserID, "err", err)
		}
		v.log.Debug("bridge token: verified from store", "user", bt.UserID, "connector", bt.Connector)
		return auth.Principal{UserID: bt.UserID, Scopes: []string{bt.Connector}, Expires: bt.ExpiresAt}, nil
	case errors.Is(err, store.ErrNotFound):
		if v.fallback != nil {
			if p, ferr := v.fallback.VerifyBridgeToken(ctx, token); ferr == nil {
				v.log.Debug("bridge token: verified by fallback (dev token)", "user", p.UserID)
				return p, nil
			}
		}
		return auth.Principal{}, auth.ErrInvalidToken
	default:
		// A store outage must read as "refused", never as "accepted"; the
		// pane retries with backoff.
		v.log.Error("bridge token: store lookup", "err", err)
		return auth.Principal{}, auth.ErrInvalidToken
	}
}
