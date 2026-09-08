package authserver

import (
	"context"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"errors"
	"net/http"
	"strings"
	"time"

	"github.com/eichler-ai/connectors/hub/internal/store"
	"github.com/eichler-ai/connectors/internal/auth"
)

// token is the token endpoint (OAuth 2.1 §4.1.3, §4.3). Public clients
// only, so there is no client authentication: the code is bound to the
// client_id and redirect_uri at issue time and the PKCE verifier proves
// possession.
func (s *Server) token(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Cache-Control", "no-store")
	if err := r.ParseForm(); err != nil {
		oauthError(w, http.StatusBadRequest, "invalid_request", "body must be application/x-www-form-urlencoded")
		return
	}
	switch r.PostForm.Get("grant_type") {
	case "authorization_code":
		s.tokenCode(w, r)
	case "refresh_token":
		s.tokenRefresh(w, r)
	default:
		oauthError(w, http.StatusBadRequest, "unsupported_grant_type", "grant_type must be authorization_code or refresh_token")
	}
}

func (s *Server) tokenCode(w http.ResponseWriter, r *http.Request) {
	f := r.PostForm
	code, clientID, verifier := f.Get("code"), f.Get("client_id"), f.Get("code_verifier")
	if code == "" || clientID == "" || verifier == "" {
		oauthError(w, http.StatusBadRequest, "invalid_request", "code, client_id and code_verifier are required")
		return
	}
	if l := len(verifier); l < 43 || l > 128 {
		oauthError(w, http.StatusBadRequest, "invalid_request", "code_verifier must be 43 to 128 characters")
		return
	}
	if res := f.Get("resource"); res != "" {
		if _, ok := s.acceptedResource(res); !ok {
			oauthError(w, http.StatusBadRequest, "invalid_target", "resource does not name this server")
			return
		}
	}
	now := s.now()
	ac, err := s.o.Store.ConsumeAuthCode(r.Context(), hashToken(code), now)
	if err != nil {
		if errors.Is(err, store.ErrReused) {
			s.log.Warn("token: authorization code replayed; family revoked", "client_id", clientID)
		} else if !errors.Is(err, store.ErrNotFound) {
			s.log.Error("token: consume code", "err", err)
			oauthError(w, http.StatusInternalServerError, "server_error", "store failure")
			return
		}
		oauthError(w, http.StatusBadRequest, "invalid_grant", "the authorization code is invalid, expired or already used")
		return
	}
	// From here the code is spent; any mismatch is a failed request AND a
	// reason to revoke what might already have been minted from it.
	sum := sha256.Sum256([]byte(verifier))
	challenge := base64.RawURLEncoding.EncodeToString(sum[:])
	switch {
	case ac.ClientID != clientID:
		s.reject(w, r.Context(), ac.ID, clientID, "code was issued to another client")
	case ac.RedirectURI != f.Get("redirect_uri"):
		s.reject(w, r.Context(), ac.ID, clientID, "redirect_uri does not match the authorization request")
	case subtle.ConstantTimeCompare([]byte(challenge), []byte(ac.CodeChallenge)) != 1:
		s.reject(w, r.Context(), ac.ID, clientID, "code_verifier does not match")
	default:
		s.issue(w, r.Context(), ac.ID, ac.UserID, ac.ClientID, ac.Scope, ac.Resource)
	}
}

func (s *Server) reject(w http.ResponseWriter, ctx context.Context, family, clientID, why string) {
	s.log.Warn("token: rejected", "client_id", clientID, "reason", why)
	if err := s.o.Store.RevokeFamily(ctx, family); err != nil {
		s.log.Error("token: revoke family", "err", err)
	}
	oauthError(w, http.StatusBadRequest, "invalid_grant", why)
}

func (s *Server) tokenRefresh(w http.ResponseWriter, r *http.Request) {
	f := r.PostForm
	tok, clientID := f.Get("refresh_token"), f.Get("client_id")
	if tok == "" || clientID == "" {
		oauthError(w, http.StatusBadRequest, "invalid_request", "refresh_token and client_id are required")
		return
	}
	if res := f.Get("resource"); res != "" {
		if _, ok := s.acceptedResource(res); !ok {
			oauthError(w, http.StatusBadRequest, "invalid_target", "resource does not name this server")
			return
		}
	}
	now := s.now()
	newTok := randomToken()
	next := store.RefreshToken{Hash: hashToken(newTok), CreatedAt: now, ExpiresAt: now.Add(refreshTokenTTL)}
	// Rotation is atomic in the store, which copies the grant's bindings
	// onto next; the old record comes back so they can be checked, and a
	// failed check revokes the family since the new token is already
	// persisted.
	old, err := s.o.Store.RotateRefreshToken(r.Context(), hashToken(tok), now, next)
	if err != nil {
		switch {
		case errors.Is(err, store.ErrReused):
			s.log.Warn("token: refresh token reused; family revoked", "client_id", clientID)
		case errors.Is(err, store.ErrRevoked), errors.Is(err, store.ErrNotFound):
		default:
			s.log.Error("token: rotate refresh token", "err", err)
			oauthError(w, http.StatusInternalServerError, "server_error", "store failure")
			return
		}
		oauthError(w, http.StatusBadRequest, "invalid_grant", "the refresh token is invalid, expired or revoked")
		return
	}
	if old.ClientID != clientID {
		s.reject(w, r.Context(), old.FamilyID, clientID, "refresh token was issued to another client")
		return
	}
	// A narrower scope applies to this access token only; the refresh
	// token keeps the original grant (RFC 6749 §6).
	scope := old.Scope
	if requested := strings.Fields(f.Get("scope")); len(requested) > 0 {
		narrowed, err := s.resolveScope(requested, "")
		if err != nil || !subset(narrowed, old.Scope) {
			s.reject(w, r.Context(), old.FamilyID, clientID, "scope must be a subset of the original grant")
			return
		}
		scope = narrowed
	}
	s.respond(w, old.UserID, old.ClientID, scope, newTok, now)
}

// issue mints the first access + refresh pair for a consumed code; family
// is the code's id.
func (s *Server) issue(w http.ResponseWriter, ctx context.Context, family, userID, clientID string, scope []string, resource string) {
	now := s.now()
	refresh := randomToken()
	rt := store.RefreshToken{
		Hash: hashToken(refresh), FamilyID: family, UserID: userID, ClientID: clientID,
		Scope: scope, Resource: resource, CreatedAt: now, ExpiresAt: now.Add(refreshTokenTTL),
	}
	if err := s.o.Store.PutRefreshToken(ctx, rt); err != nil {
		s.log.Error("token: store refresh token", "err", err)
		oauthError(w, http.StatusInternalServerError, "server_error", "store failure")
		return
	}
	s.respond(w, userID, clientID, scope, refresh, now)
}

func (s *Server) respond(w http.ResponseWriter, userID, clientID string, scope []string, refresh string, now time.Time) {
	exp := now.Add(auth.AccessTokenTTL)
	access, err := s.o.Keys.Sign(auth.Claims{
		Issuer:   s.o.Issuer,
		Subject:  userID,
		Audience: auth.Audience{s.o.Issuer},
		Scope:    strings.Join(scope, " "),
		ClientID: clientID,
		JTI:      randomToken()[:22],
		IssuedAt: now.Unix(),
		Expires:  exp.Unix(),
	})
	if err != nil {
		s.log.Error("token: sign", "err", err)
		oauthError(w, http.StatusInternalServerError, "server_error", "could not sign the token")
		return
	}
	s.log.Info("token: issued", "user", userID, "client_id", clientID, "scope", strings.Join(scope, " "))
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Pragma", "no-cache")
	_ = json.NewEncoder(w).Encode(map[string]any{
		"access_token":  access,
		"token_type":    "Bearer",
		"expires_in":    int(auth.AccessTokenTTL / time.Second),
		"refresh_token": refresh,
		"scope":         strings.Join(scope, " "),
	})
}
