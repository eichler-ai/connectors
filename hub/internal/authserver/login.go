package authserver

import (
	"errors"
	"net/http"
	"strings"

	"github.com/eichler-ai/connectors/hub/internal/graphtoken"
	"github.com/eichler-ai/connectors/hub/internal/store"
)

func (s *Server) callbackURL() string { return s.o.Issuer + "/login/microsoft/callback" }

// loginStart sends the user to the provider from the sign-in page's button.
func (s *Server) loginStart(w http.ResponseWriter, r *http.Request) {
	ls, ok := s.loadLoginState(w, r, r.URL.Query().Get("ls"))
	if !ok {
		return
	}
	s.startLogin(w, r, ls)
}

// loginSwitch is the consent page's "Not you? Use a different account"
// link: GET /login/switch?ls=<id>. It clears the hub's own session and
// re-runs the same login state through startLogin, which reaches Microsoft
// again (unlike a session hit, which skips it) and, because select_account
// is already in AuthURL, shows the account chooser. The callback lands back
// on /oauth/consent?ls=<id> as whichever account the user picks.
func (s *Server) loginSwitch(w http.ResponseWriter, r *http.Request) {
	ls, ok := s.loadLoginState(w, r, r.URL.Query().Get("ls"))
	if !ok {
		return
	}
	s.clearSession(w)
	s.startLogin(w, r, ls)
}

// startLogin stores ls with a fresh nonce and PKCE verifier and redirects
// to the provider. The login state id is the provider's `state`, so the
// callback can find the pending request without a cookie (the session
// cookie does not exist yet). Shared by the OAuth sign-in page and the
// pane's /bridge/authorize, which differ only in what ls says to do after.
func (s *Server) startLogin(w http.ResponseWriter, r *http.Request, ls store.LoginState) {
	if s.o.Provider == nil {
		s.errorPage(w, http.StatusNotImplemented, "Sign-in unavailable", "No identity provider is configured on this server.")
		return
	}
	ls.Nonce = randomToken()
	ls.ProviderPKCE = randomToken()
	if err := s.o.Store.PutLoginState(r.Context(), ls); err != nil {
		s.log.Error("login: store login state", "err", err)
		s.errorPage(w, http.StatusInternalServerError, "Sign-in failed", "The sign-in could not be started.")
		return
	}
	u, err := s.o.Provider.AuthURL(r.Context(), s.callbackURL(), ls.ID, ls.Nonce, ls.ProviderPKCE)
	if err != nil {
		s.log.Error("login: provider discovery", "err", err)
		s.errorPage(w, http.StatusBadGateway, "Sign-in failed", "The identity provider could not be reached.")
		return
	}
	w.Header().Set("Cache-Control", "no-store")
	http.Redirect(w, r, u, http.StatusFound)
}

// loginCallback completes the provider round trip: validates the ID token,
// finds or creates the user, sets the session cookie and moves on to
// consent — or, for a pane sign-in, back to the page that started it.
func (s *Server) loginCallback(w http.ResponseWriter, r *http.Request) {
	if s.o.Provider == nil {
		s.errorPage(w, http.StatusNotImplemented, "Sign-in unavailable", "No identity provider is configured on this server.")
		return
	}
	q := r.URL.Query()
	ls, ok := s.loadLoginState(w, r, q.Get("state"))
	if !ok {
		return
	}
	if e := q.Get("error"); e != "" {
		s.log.Info("login: provider returned an error", "error", e)
		_ = s.o.Store.DeleteLoginState(r.Context(), ls.ID)
		if ls.Return != "" {
			// No OAuth client to send the error to: the page sits in the
			// Office dialog, and the user closes it.
			s.errorPage(w, http.StatusBadRequest, "Sign-in cancelled", "Microsoft did not complete the sign-in ("+e+"). Close this window and try again from the add-in.")
			return
		}
		s.redirectError(w, r, ls.RedirectURI, ls.State, "access_denied", "sign-in was cancelled or refused: "+e)
		return
	}
	code := q.Get("code")
	if code == "" || ls.Nonce == "" {
		s.errorPage(w, http.StatusBadRequest, "Sign-in failed", "The identity provider's response was incomplete.")
		return
	}
	// The nonce is single-use: a replayed callback finds none.
	nonce, verifier := ls.Nonce, ls.ProviderPKCE
	ls.Nonce, ls.ProviderPKCE = "", ""
	if err := s.o.Store.PutLoginState(r.Context(), ls); err != nil {
		s.log.Error("login: update login state", "err", err)
	}
	id, err := s.o.Provider.Exchange(r.Context(), code, s.callbackURL(), verifier, nonce)
	if err != nil {
		s.log.Warn("login: exchange failed", "err", err)
		s.errorPage(w, http.StatusBadGateway, "Sign-in failed", "Microsoft did not complete the sign-in. "+err.Error())
		return
	}
	user, err := s.upsertUser(r, id)
	if err != nil {
		s.log.Error("login: store user", "err", err)
		s.errorPage(w, http.StatusInternalServerError, "Sign-in failed", "Your account could not be recorded.")
		return
	}
	// The Microsoft refresh token AuthURL's upfront consent earns (RFC
	// excel/docs/rfc-graph-create-and-open.md §3.1), encrypted before it
	// ever reaches the store. Best-effort: a user who somehow signs in
	// without one (a provider that doesn't grant offline_access) still gets
	// a session — create_workbook is what reports graph-not-connected, not
	// sign-in. Never logged past this point.
	if id.GraphRefreshToken != "" {
		if err := graphtoken.Put(r.Context(), s.o.Store, s.o.Keys, user.ID, id.GraphRefreshToken, s.now()); err != nil {
			s.log.Error("login: store graph token", "user", user.ID, "err", err)
		} else {
			s.log.Info("login: graph token stored", "user", user.ID)
		}
	}
	s.setSession(w, user.ID)
	w.Header().Set("Cache-Control", "no-store")
	if ls.Return != "" {
		// The pane flow is done with its login state here; the OAuth flow
		// spends its own at consent. Return was written by /bridge/authorize
		// from validated parameters, and the check below keeps a corrupted
		// record from ever becoming an open redirect.
		_ = s.o.Store.DeleteLoginState(r.Context(), ls.ID)
		if !strings.HasPrefix(ls.Return, "/") || strings.HasPrefix(ls.Return, "//") {
			s.log.Error("login: login state has a non-local return", "return", ls.Return)
			s.errorPage(w, http.StatusInternalServerError, "Sign-in failed", "The sign-in could not be completed.")
			return
		}
		http.Redirect(w, r, ls.Return, http.StatusFound)
		return
	}
	http.Redirect(w, r, "/oauth/consent?ls="+ls.ID, http.StatusFound)
}

// upsertUser finds the user for the provider identity or creates one with
// a fresh random id; profile fields refresh on every login.
func (s *Server) upsertUser(r *http.Request, id Identity) (store.User, error) {
	now := s.now()
	u, err := s.o.Store.UserByIdentity(r.Context(), s.o.Provider.Name, id.Subject)
	switch {
	case errors.Is(err, store.ErrNotFound):
		u = store.User{ID: "u_" + randomToken()[:22], Provider: s.o.Provider.Name, Subject: id.Subject, CreatedAt: now}
		s.log.Info("login: new user", "user", u.ID, "provider", u.Provider)
	case err != nil:
		return store.User{}, err
	default:
		s.log.Info("login: user signed in", "user", u.ID, "provider", u.Provider)
	}
	u.Tenant, u.Email, u.DisplayName, u.LastLoginAt = id.Tenant, id.Email, id.DisplayName, now
	if err := s.o.Store.PutUser(r.Context(), u); err != nil {
		return store.User{}, err
	}
	return u, nil
}
