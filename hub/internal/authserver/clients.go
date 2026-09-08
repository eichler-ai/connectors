package authserver

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"unicode/utf8"

	"github.com/eichler-ai/connectors/hub/internal/store"
)

// client is a resolved OAuth client, from either registration path.
type client struct {
	ID           string
	Name         string
	RedirectURIs []string
	// Stored is true for a DCR client (which TouchClient marks used) and
	// false for a metadata-document client.
	Stored bool
}

// resolveClient finds the client behind a client_id: an https URL is a
// Client ID Metadata Document, anything else is a dynamic registration.
func (s *Server) resolveClient(ctx context.Context, id string) (client, error) {
	if strings.HasPrefix(id, "https://") {
		return s.cimd.resolve(ctx, id)
	}
	c, err := s.o.Store.Client(ctx, id)
	if err != nil {
		return client{}, err
	}
	return client{ID: c.ID, Name: c.Name, RedirectURIs: c.RedirectURIs, Stored: true}, nil
}

// allowedRedirect reports whether the request's redirect_uri matches one the
// client registered: exact string comparison (OAuth 2.1 §4.1.1), except that
// a registered plain-http loopback URI matches any port (RFC 8252 §7.3),
// since a native client such as Claude Code listens on whatever port is
// free and registers http://localhost/callback. Scheme, host and path must
// still be identical; only the port may differ.
func (c client) allowedRedirect(uri string) bool {
	for _, r := range c.RedirectURIs {
		if r == uri {
			return true
		}
	}
	req, err := url.Parse(uri)
	if err != nil || req.Scheme != "http" || !isLoopbackHost(req.Hostname()) || req.Fragment != "" || req.User != nil {
		return false
	}
	for _, r := range c.RedirectURIs {
		reg, err := url.Parse(r)
		if err != nil || reg.Scheme != "http" || !isLoopbackHost(reg.Hostname()) {
			continue
		}
		if reg.Hostname() == req.Hostname() && reg.Path == req.Path && reg.RawQuery == req.RawQuery {
			return true
		}
	}
	return false
}

// validRedirectURI is the registration rule from the brief and the MCP
// spec: https, or plain http to a loopback host on any port (Claude Code
// and Claude Desktop listen on localhost). No fragments, no custom schemes.
func validRedirectURI(raw string) error {
	u, err := url.Parse(raw)
	if err != nil || u.Host == "" || u.Fragment != "" || u.User != nil {
		return fmt.Errorf("redirect_uri %q is not an absolute URL without fragment", raw)
	}
	switch u.Scheme {
	case "https":
		return nil
	case "http":
		if isLoopbackHost(u.Hostname()) {
			return nil
		}
		return fmt.Errorf("redirect_uri %q: http is only allowed for localhost", raw)
	}
	return fmt.Errorf("redirect_uri %q: scheme must be https or http://localhost", raw)
}

func isLoopbackHost(h string) bool {
	if h == "localhost" {
		return true
	}
	ip := net.ParseIP(h)
	return ip != nil && ip.IsLoopback()
}

// Registration limits: a client that needs more is not a Claude client.
const (
	maxRegistrationBody = 16 << 10
	maxRedirectURIs     = 10
	maxClientName       = 100
)

// registration is the RFC 7591 request subset we act on; the rest is
// accepted and ignored.
type registration struct {
	RedirectURIs            []string `json:"redirect_uris"`
	TokenEndpointAuthMethod string   `json:"token_endpoint_auth_method,omitempty"`
	GrantTypes              []string `json:"grant_types,omitempty"`
	ResponseTypes           []string `json:"response_types,omitempty"`
	ClientName              string   `json:"client_name,omitempty"`
}

func (reg *registration) validate() error {
	if len(reg.RedirectURIs) == 0 {
		return errors.New("redirect_uris is required")
	}
	if len(reg.RedirectURIs) > maxRedirectURIs {
		return fmt.Errorf("at most %d redirect_uris", maxRedirectURIs)
	}
	for _, u := range reg.RedirectURIs {
		if err := validRedirectURI(u); err != nil {
			return err
		}
	}
	if reg.TokenEndpointAuthMethod != "" && reg.TokenEndpointAuthMethod != "none" {
		return errors.New("only public clients are supported: token_endpoint_auth_method must be \"none\"")
	}
	if !subset(reg.GrantTypes, []string{"authorization_code", "refresh_token"}) {
		return errors.New("grant_types may only contain authorization_code and refresh_token")
	}
	if !subset(reg.ResponseTypes, []string{"code"}) {
		return errors.New("response_types may only contain code")
	}
	reg.ClientName = strings.TrimSpace(reg.ClientName)
	if !utf8.ValidString(reg.ClientName) || utf8.RuneCountInString(reg.ClientName) > maxClientName {
		return fmt.Errorf("client_name must be valid UTF-8 of at most %d characters", maxClientName)
	}
	return nil
}

// register is dynamic client registration (RFC 7591), public clients only.
func (s *Server) register(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Cache-Control", "no-store")
	if !s.reg.allow(s.clientIP(r), s.now()) {
		oauthError(w, http.StatusTooManyRequests, "invalid_request", "too many registrations from this address; try again in a minute")
		return
	}
	body, err := io.ReadAll(io.LimitReader(r.Body, maxRegistrationBody+1))
	if err != nil || len(body) > maxRegistrationBody {
		oauthError(w, http.StatusBadRequest, "invalid_client_metadata", "request body missing or too large")
		return
	}
	var reg registration
	if err := json.Unmarshal(body, &reg); err != nil {
		oauthError(w, http.StatusBadRequest, "invalid_client_metadata", "body is not a JSON object")
		return
	}
	if err := reg.validate(); err != nil {
		code := "invalid_client_metadata"
		if strings.HasPrefix(err.Error(), "redirect_uri") {
			code = "invalid_redirect_uri"
		}
		oauthError(w, http.StatusBadRequest, code, err.Error())
		return
	}
	now := s.now()
	c := store.Client{ID: "dcr_" + randomToken(), Name: reg.ClientName, RedirectURIs: reg.RedirectURIs, CreatedAt: now}
	if err := s.o.Store.PutClient(r.Context(), c); err != nil {
		s.log.Error("authserver: register: store", "err", err)
		oauthError(w, http.StatusInternalServerError, "server_error", "could not store the registration")
		return
	}
	s.log.Info("authserver: client registered", "client_id", c.ID, "redirect_uris", len(c.RedirectURIs))
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusCreated)
	_ = json.NewEncoder(w).Encode(map[string]any{
		"client_id":                  c.ID,
		"client_id_issued_at":        now.Unix(),
		"client_name":                c.Name,
		"redirect_uris":              c.RedirectURIs,
		"token_endpoint_auth_method": "none",
		"grant_types":                []string{"authorization_code", "refresh_token"},
		"response_types":             []string{"code"},
	})
}
