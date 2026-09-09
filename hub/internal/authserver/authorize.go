package authserver

import (
	"errors"
	"net/http"
	"net/url"
	"strings"

	"github.com/eichler-ai/connectors/hub/internal/store"
)

// authorize is the authorization endpoint (OAuth 2.1 §4.1.1 with PKCE
// S256 mandatory, RFC 8707 resource required). Client and redirect_uri are
// checked before anything else and their failures render a page rather
// than redirect; everything after that goes back to the client as an
// error redirect with its state.
func (s *Server) authorize(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	clientID := q.Get("client_id")
	if clientID == "" {
		s.errorPage(w, http.StatusBadRequest, "Missing client", "The request did not name a client (client_id).")
		return
	}
	cl, err := s.resolveClient(r.Context(), clientID)
	if err != nil {
		s.log.Info("authorize: unknown client", "client_id", clientID, "err", err)
		msg := "The client is not registered with this server."
		if !errors.Is(err, store.ErrNotFound) {
			msg = "The client's metadata could not be used: " + err.Error()
		}
		s.errorPage(w, http.StatusBadRequest, "Unknown client", msg)
		return
	}
	redirectURI := q.Get("redirect_uri")
	if redirectURI == "" || !cl.allowedRedirect(redirectURI) {
		s.log.Info("authorize: redirect_uri mismatch", "client_id", clientID)
		s.errorPage(w, http.StatusBadRequest, "Redirect not allowed", "The redirect address is not one this client registered.")
		return
	}
	state := q.Get("state")
	fail := func(code, desc string) {
		s.log.Info("authorize: rejected", "client_id", clientID, "error", code, "detail", desc)
		s.redirectError(w, r, redirectURI, state, code, desc)
	}
	if state == "" {
		fail("invalid_request", "state is required")
		return
	}
	if q.Get("response_type") != "code" {
		fail("unsupported_response_type", "only response_type=code is supported")
		return
	}
	challenge := q.Get("code_challenge")
	if challenge == "" || q.Get("code_challenge_method") != "S256" {
		fail("invalid_request", "PKCE with code_challenge_method=S256 is required")
		return
	}
	if len(challenge) != 43 {
		fail("invalid_request", "code_challenge must be the base64url SHA-256 of the verifier")
		return
	}
	resource := q.Get("resource")
	if resource == "" {
		fail("invalid_target", "resource is required (RFC 8707); use the MCP server URL")
		return
	}
	resourceSlug, ok := s.acceptedResource(resource)
	if !ok {
		fail("invalid_target", "resource does not name this server")
		return
	}
	scope, err := s.resolveScope(strings.Fields(q.Get("scope")), resourceSlug)
	if err != nil {
		fail("invalid_scope", err.Error())
		return
	}

	ls := store.LoginState{
		ID:            randomToken(),
		ClientID:      cl.ID,
		ClientName:    cl.Name,
		RedirectURI:   redirectURI,
		State:         state,
		CodeChallenge: challenge,
		Scope:         scope,
		Resource:      resource,
		ExpiresAt:     s.now().Add(loginStateTTL),
	}
	if err := s.o.Store.PutLoginState(r.Context(), ls); err != nil {
		s.log.Error("authorize: store login state", "err", err)
		fail("server_error", "could not start the authorization")
		return
	}
	if s.sessionUser(r) != "" {
		s.renderConsent(w, r, ls)
		return
	}
	s.render(w, http.StatusOK, "signin", pageData{Title: "Sign in", ClientName: cl.Name, LS: ls.ID})
}

// redirectError sends an OAuth error back to the (already validated)
// redirect URI, with state and iss (RFC 9207).
func (s *Server) redirectError(w http.ResponseWriter, r *http.Request, redirectURI, state, code, desc string) {
	s.redirectWith(w, r, redirectURI, url.Values{"error": {code}, "error_description": {desc}, "state": {state}})
}

func (s *Server) redirectWith(w http.ResponseWriter, r *http.Request, redirectURI string, params url.Values) {
	u, _ := url.Parse(redirectURI)
	q := u.Query()
	for k, v := range params {
		q[k] = v
	}
	q.Set("iss", s.o.Issuer)
	u.RawQuery = q.Encode()
	w.Header().Set("Cache-Control", "no-store")
	http.Redirect(w, r, u.String(), http.StatusFound)
}

func (s *Server) renderConsent(w http.ResponseWriter, r *http.Request, ls store.LoginState) {
	u, _ := url.Parse(ls.RedirectURI)
	s.render(w, http.StatusOK, "consent", pageData{
		Title:        "Allow access",
		ClientName:   ls.ClientName,
		Scopes:       ls.Scope,
		RedirectHost: u.Host,
		Loopback:     isLoopbackHost(u.Hostname()),
		LS:           ls.ID,
		SignedInAs:   s.signedInLabel(r),
		// scheme://host[:port] is the CSP source expression form; approve
		// and deny both redirect there. The URI was validated at authorize
		// time, so it cannot carry anything but a scheme, host and port.
		FormActionOrigins: []string{u.Scheme + "://" + u.Host},
	})
}

// signedInLabel is what the consent page shows for "signed in as": the
// session user's email, falling back to their display name or a generic
// label — never empty, and never the raw user id.
func (s *Server) signedInLabel(r *http.Request) string {
	userID := s.sessionUser(r)
	if userID == "" {
		return ""
	}
	user, err := s.o.Store.User(r.Context(), userID)
	if err != nil {
		return "your Microsoft account"
	}
	if user.Email != "" {
		return user.Email
	}
	if user.DisplayName != "" {
		return user.DisplayName
	}
	return "your Microsoft account"
}

// consentGet shows the consent page after sign-in.
func (s *Server) consentGet(w http.ResponseWriter, r *http.Request) {
	ls, ok := s.loadLoginState(w, r, r.URL.Query().Get("ls"))
	if !ok {
		return
	}
	if s.sessionUser(r) == "" {
		s.render(w, http.StatusOK, "signin", pageData{Title: "Sign in", ClientName: ls.ClientName, LS: ls.ID})
		return
	}
	s.renderConsent(w, r, ls)
}

// consentPost records the decision: approve mints a code bound to the
// user, client, redirect URI, PKCE challenge, scope and resource; deny
// sends access_denied. Either way the login state is spent.
func (s *Server) consentPost(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseForm(); err != nil {
		s.errorPage(w, http.StatusBadRequest, "Bad request", "The form could not be read.")
		return
	}
	ls, ok := s.loadLoginState(w, r, r.PostForm.Get("ls"))
	if !ok {
		return
	}
	userID := s.sessionUser(r)
	if userID == "" {
		s.errorPage(w, http.StatusUnauthorized, "Signed out", "Your sign-in expired before you decided. Start again from your Claude client.")
		return
	}
	// Single use: whichever outcome, the state is gone before the redirect.
	_ = s.o.Store.DeleteLoginState(r.Context(), ls.ID)
	if r.PostForm.Get("decision") != "approve" {
		s.log.Info("consent: denied", "user", userID, "client_id", ls.ClientID)
		s.redirectError(w, r, ls.RedirectURI, ls.State, "access_denied", "the user denied the request")
		return
	}
	code := randomToken()
	ac := store.AuthCode{
		Hash:          hashToken(code),
		ID:            randomToken(),
		ClientID:      ls.ClientID,
		RedirectURI:   ls.RedirectURI,
		CodeChallenge: ls.CodeChallenge,
		UserID:        userID,
		Scope:         ls.Scope,
		Resource:      ls.Resource,
		ExpiresAt:     s.now().Add(authCodeTTL),
	}
	if err := s.o.Store.PutAuthCode(r.Context(), ac); err != nil {
		s.log.Error("consent: store code", "err", err)
		s.redirectError(w, r, ls.RedirectURI, ls.State, "server_error", "could not issue the authorization code")
		return
	}
	if !strings.HasPrefix(ls.ClientID, "https://") {
		if err := s.o.Store.TouchClient(r.Context(), ls.ClientID, s.now()); err != nil {
			s.log.Warn("consent: touch client", "client_id", ls.ClientID, "err", err)
		}
	}
	s.log.Info("consent: approved", "user", userID, "client_id", ls.ClientID, "scope", strings.Join(ls.Scope, " "))
	s.redirectWith(w, r, ls.RedirectURI, url.Values{"code": {code}, "state": {ls.State}})
}

// loadLoginState fetches the in-flight request or renders the expiry page.
func (s *Server) loadLoginState(w http.ResponseWriter, r *http.Request, id string) (store.LoginState, bool) {
	if id == "" {
		s.errorPage(w, http.StatusBadRequest, "Bad request", "This page needs to be reached from your Claude client's connect flow.")
		return store.LoginState{}, false
	}
	ls, err := s.o.Store.LoginState(r.Context(), id)
	if err != nil {
		s.errorPage(w, http.StatusBadRequest, "Request expired", "This sign-in request has expired or was already completed. Start again from your Claude client.")
		return store.LoginState{}, false
	}
	return ls, true
}
