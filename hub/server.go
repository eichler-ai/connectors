package hub

import (
	"bytes"
	"errors"
	"fmt"
	"io/fs"
	"log/slog"
	"net/http"
	"net/url"
	"strings"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/hub/auth"
	"github.com/eichler-ai/connectors/hub/internal/bridge"
	"github.com/eichler-ai/connectors/hub/internal/registry"
)

// DevPublicURL is the origin the hub serves in -dev mode and the one every
// connector writes into its manifest; the hub rewrites it to the real public
// origin when serving the manifest, so one file serves both deployments.
const DevPublicURL = "https://localhost:8443"

// Options configure a hub.
type Options struct {
	// PublicURL is the externally visible origin (scheme://host[:port]).
	PublicURL string
	// Auth verifies MCP bearer tokens and bridge hello tokens.
	Auth auth.Authenticator
	// AllowedOrigins are extra browser origins permitted on the bridge
	// sockets (the hub's own origin is always allowed).
	AllowedOrigins []string
	Connectors     []Connector
	// Version is reported as each MCP server's version and by get_skills.
	Version string
	Logger  *slog.Logger
	// Bridge holds socket tuning; tests shorten the intervals.
	Bridge bridge.Options
	// UserOf overrides how tools learn the calling user; nil means "from the
	// bearer token", which is the only production choice. See Host.userOf.
	UserOf func(req *mcp.CallToolRequest) (string, bool)
}

// Server is one hub process: registry, bridge service, and one MCP server per
// connector, exposed through Handler.
type Server struct {
	opts    Options
	host    *Host
	handler http.Handler
	servers map[string]*mcp.Server
}

// NewServer wires everything up. It does not listen; cmd/hub does.
func NewServer(opts Options) (*Server, error) {
	if opts.Auth == nil {
		return nil, errors.New("hub: Options.Auth is required")
	}
	if len(opts.Connectors) == 0 {
		return nil, errors.New("hub: at least one connector is required")
	}
	if opts.PublicURL == "" {
		opts.PublicURL = DevPublicURL
	}
	pub, err := url.Parse(opts.PublicURL)
	if err != nil || pub.Scheme == "" || pub.Host == "" {
		return nil, fmt.Errorf("hub: PublicURL %q is not an absolute URL", opts.PublicURL)
	}
	opts.PublicURL = strings.TrimSuffix(opts.PublicURL, "/")
	if opts.Logger == nil {
		opts.Logger = slog.Default()
	}
	if opts.Version == "" {
		opts.Version = "dev"
	}

	reg := registry.New()
	bopts := opts.Bridge
	bopts.AllowedOrigins = append(append([]string{pub.Scheme + "://" + pub.Host}, opts.AllowedOrigins...), bopts.AllowedOrigins...)
	bopts.Logger = opts.Logger
	bridges := bridge.New(reg, opts.Auth, bopts)

	host := &Host{reg: reg, bridges: bridges, log: opts.Logger, userOf: opts.UserOf, defaultTimeout: DefaultTimeout, maxTimeout: MaxTimeout}
	if host.userOf == nil {
		host.userOf = userFromToken
	}

	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/plain")
		_, _ = w.Write([]byte("ok\n"))
	})
	s := &Server{opts: opts, host: host, handler: mux, servers: map[string]*mcp.Server{}}
	requireBearer := auth.RequireBearer(opts.Auth)
	for _, c := range opts.Connectors {
		slug := c.Slug()
		if slug == "" || strings.ContainsAny(slug, "/ ") || s.servers[slug] != nil {
			return nil, fmt.Errorf("hub: connector slug %q is empty, duplicate, or not a path segment", slug)
		}
		srv := mcp.NewServer(&mcp.Implementation{Name: slug, Version: opts.Version}, nil)
		registerGenericTools(srv, host, c, opts.Version)
		c.Tools(&ToolRegistry{Server: srv, Host: host})
		s.servers[slug] = srv

		mcpHandler := mcp.NewStreamableHTTPHandler(func(*http.Request) *mcp.Server { return srv }, &mcp.StreamableHTTPOptions{
			// Stateless: every request is its own session. Nothing in v1 needs
			// server-initiated requests, so there is no per-session state to
			// keep — which means nothing to retain or expire, nothing lost on a
			// deploy, and no sticky routing needed when a second instance
			// arrives. The user is still bound per request by the bearer token.
			Stateless: true,
			Logger:    opts.Logger,
		})
		mux.Handle("/"+slug+"/mcp", requireBearer(mcpHandler))
		if c.Capabilities().Bridge {
			mux.Handle("GET /"+slug+"/bridge", bridges.Handler(slug))
		}
		if static := c.Static(); static != nil {
			mux.Handle("GET /"+slug+"/addin/", http.StripPrefix("/"+slug+"/addin/", http.FileServerFS(static)))
			mux.HandleFunc("GET /"+slug+"/manifest.xml", s.manifestHandler(static))
		}
	}
	return s, nil
}

// Handler is the hub's whole HTTP surface.
func (s *Server) Handler() http.Handler { return s.handler }

// Host exposes the services for tests and cmd/hub.
func (s *Server) Host() *Host { return s.host }

// MCPServer returns the connector's mcp.Server, for tests that connect to it
// over the in-memory transport.
func (s *Server) MCPServer(slug string) *mcp.Server { return s.servers[slug] }

// manifestHandler serves the connector's manifest with DevPublicURL rewritten
// to the real public origin. Office needs every URL in the manifest to be
// absolute, and the same file must work sideloaded against a laptop.
func (s *Server) manifestHandler(static fs.FS) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		b, err := fs.ReadFile(static, "manifest.xml")
		if err != nil {
			http.NotFound(w, r)
			return
		}
		if s.opts.PublicURL != DevPublicURL {
			b = bytes.ReplaceAll(b, []byte(DevPublicURL), []byte(s.opts.PublicURL))
		}
		w.Header().Set("Content-Type", "application/xml")
		_, _ = w.Write(b)
	}
}

// userFromToken is the production Host.userOf: the bearer middleware put a
// TokenInfo on the request and the SDK carries it into every tool call.
func userFromToken(req *mcp.CallToolRequest) (string, bool) {
	if req == nil || req.Extra == nil || req.Extra.TokenInfo == nil {
		return "", false
	}
	return req.Extra.TokenInfo.UserID, req.Extra.TokenInfo.UserID != ""
}
