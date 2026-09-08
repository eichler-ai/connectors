package hub

import (
	"bytes"
	"crypto/sha256"
	"errors"
	"fmt"
	"io/fs"
	"log/slog"
	"net/http"
	"net/url"
	"regexp"
	"strings"

	mcpauth "github.com/modelcontextprotocol/go-sdk/auth"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"github.com/modelcontextprotocol/go-sdk/oauthex"

	"github.com/eichler-ai/connectors/hub/internal/bridge"
	"github.com/eichler-ai/connectors/hub/internal/registry"
	"github.com/eichler-ai/connectors/hub/wellknown"
	"github.com/eichler-ai/connectors/internal/auth"
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
	// AuthServer mounts the authorization server's routes at the issuer
	// root (PRD §05). Nil leaves only the resource-server side: the MCP
	// endpoints still demand tokens and advertise PublicURL as their
	// issuer, which is what tests of the bridge side want.
	AuthServer interface{ Routes(mux *http.ServeMux) }
	// AllowedOrigins are extra browser origins permitted on the bridge
	// sockets (the hub's own origin is always allowed).
	AllowedOrigins []string
	Connectors     []Connector
	// Version is reported as each MCP server's version and by get_skills.
	Version string
	// Environment is "prod", "staging" or "dev" (default). Excel for the web
	// keys a sideloaded add-in by the manifest <Id>, so a hub that is not
	// "prod" gets a deterministic Id and DisplayName suffix derived from
	// Environment and PublicURL — see manifestHandler — so a dev or staging
	// add-in never collides with (or silently replaces) the production one
	// in a user's My Add-ins list. Production serves the manifest file's Id
	// and name unchanged, since that is what the Store submission carries.
	Environment string
	Logger      *slog.Logger
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
	switch opts.Environment {
	case "":
		opts.Environment = "dev"
	case "prod", "staging", "dev":
	default:
		return nil, fmt.Errorf("hub: Options.Environment %q is not prod, staging or dev", opts.Environment)
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
	// Not /healthz: Cloud Run's frontend answers that exact path itself
	// (a platform quirk) before it ever reaches this container.
	mux.HandleFunc("GET /health", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/plain")
		// The deploy check reads the version here now that /mcp needs a
		// user's token to answer anything.
		w.Header().Set("Hub-Version", opts.Version)
		_, _ = w.Write([]byte("ok\n"))
	})
	mux.HandleFunc("GET /.well-known/microsoft-identity-association.json", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(wellknown.MicrosoftIdentityAssociation)
	})
	s := &Server{opts: opts, host: host, handler: mux, servers: map[string]*mcp.Server{}}
	if opts.AuthServer != nil {
		opts.AuthServer.Routes(mux)
	}
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
		// Resource metadata (RFC 9728) per connector: the resource is this
		// connector's MCP URL — the canonical form every MCP client sends as
		// its `resource` and the only form the go-sdk client accepts — while
		// the authorization server issues one audience for the whole hub and
		// this connector's slug as the scope (§18.3; authserver.acceptedResource).
		metadataURL := opts.PublicURL + "/" + slug + "/.well-known/oauth-protected-resource"
		prm := mcpauth.ProtectedResourceMetadataHandler(&oauthex.ProtectedResourceMetadata{
			Resource:               opts.PublicURL + "/" + slug + "/mcp",
			AuthorizationServers:   []string{opts.PublicURL},
			ScopesSupported:        []string{slug},
			BearerMethodsSupported: []string{"header"},
			ResourceName:           "Eichler Connectors: " + slug,
		})
		mux.Handle("/"+slug+"/.well-known/oauth-protected-resource", prm)
		// Also at RFC 9728's path-insertion location, which a client that
		// ignores the WWW-Authenticate pointer probes first.
		mux.Handle("/.well-known/oauth-protected-resource/"+slug+"/mcp", prm)
		requireBearer := auth.RequireBearer(opts.Auth, auth.BearerOptions{ResourceMetadataURL: metadataURL, Scopes: []string{slug}})
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

// PublicURL is the normalised public origin, which is also the issuer.
func (s *Server) PublicURL() string { return s.opts.PublicURL }

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
		if s.opts.Environment != "prod" {
			b = rewriteManifestIdentity(b, s.opts.Environment, s.opts.PublicURL)
		}
		w.Header().Set("Content-Type", "application/xml")
		_, _ = w.Write(b)
	}
}

var (
	manifestIDPattern          = regexp.MustCompile(`<Id>[^<]*</Id>`)
	manifestDisplayNamePattern = regexp.MustCompile(`(<DisplayName DefaultValue=")([^"]*)("\s*/>)`)
	// The ribbon shows these two resource strings (excel/addin/manifest.xml's
	// bt:ShortStrings), not DisplayName — Office reads DisplayName only for
	// My Add-ins/the store listing. Without also suffixing them, dev, staging
	// and prod sideloads look identical in the Home tab.
	manifestRibbonLabelPattern = regexp.MustCompile(`(<bt:String id="Bridge\.(?:Group|Open)\.Label" DefaultValue=")([^"]*)("\s*/>)`)
)

// rewriteManifestIdentity replaces a non-production manifest's <Id> with a
// deterministic per-environment UUID and appends " (<environment>)" to its
// DisplayName and ribbon group/button labels, so Excel for the web — which
// keys a sideloaded add-in by Id — never conflates a dev or staging add-in
// with production, or with each other, and a user can tell them apart both
// in My Add-ins and on the ribbon itself.
func rewriteManifestIdentity(b []byte, environment, publicURL string) []byte {
	id := manifestID(environment, publicURL)
	suffix := []byte(` (` + environment + `)${3}`)
	b = manifestIDPattern.ReplaceAll(b, []byte("<Id>"+id+"</Id>"))
	b = manifestDisplayNamePattern.ReplaceAll(b, append([]byte(`${1}${2}`), suffix...))
	b = manifestRibbonLabelPattern.ReplaceAll(b, append([]byte(`${1}${2}`), suffix...))
	return b
}

// manifestID derives a stable, valid v4-shaped UUID from environment and
// publicURL: the first 16 bytes of sha256("connectors-addin:"+environment+
// ":"+publicURL), with the version and variant bits set as RFC 4122
// requires. Deterministic (same inputs, same output every call) rather than
// random, so a redeploy of the same environment doesn't churn the add-in's
// identity and force every user to re-sideload it.
func manifestID(environment, publicURL string) string {
	sum := sha256.Sum256([]byte("connectors-addin:" + environment + ":" + publicURL))
	b := sum[:16]
	b[6] = (b[6] & 0x0f) | 0x40 // version 4
	b[8] = (b[8] & 0x3f) | 0x80 // variant 10
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

// userFromToken is the production Host.userOf: the bearer middleware put a
// TokenInfo on the request and the SDK carries it into every tool call.
func userFromToken(req *mcp.CallToolRequest) (string, bool) {
	if req == nil || req.Extra == nil || req.Extra.TokenInfo == nil {
		return "", false
	}
	return req.Extra.TokenInfo.UserID, req.Extra.TokenInfo.UserID != ""
}
