// hub is the Connectors Hub server (hub/docs/PRD.md): one process serving every
// connector's MCP endpoint, bridge socket and extension files.
//
//	hub                  serve plain HTTP on $PORT (Cloud Run; TLS terminated upstream)
//	hub -dev             serve HTTPS on https://localhost:8443 with a self-signed certificate,
//	                     so the add-in can be sideloaded against this laptop
//	hub -trust-cert      print the command that trusts the -dev certificate
//	hub revoke-user ID   revoke every refresh token of a user (PRD §13 kill switch); the
//	                     user's access tokens expire on their own within 15 minutes
//
// Environment:
//
//	PORT                   listen port (Cloud Run sets it); default 8080, or 8443 with -dev
//	HUB_PUBLIC_URL         externally visible origin, e.g. https://connectors.eichler.ai;
//	                       default https://localhost:8443 (rewritten into served manifests);
//	                       also the OAuth issuer and the audience of every access token
//	HUB_JWT_SIGNING_KEY    PEM with one or more P-256 private keys; the first signs, all
//	                       verify (rotation: prepend). Secret Manager hub-jwt-signing-key.
//	                       -dev without it uses (and creates) <data dir>/jwt-signing-key.pem
//	HUB_MS_CLIENT_ID       Entra application (client) id for Microsoft sign-in
//	HUB_MS_CLIENT_SECRET   its client secret (Secret Manager entra-client-secret). Never logged.
//	HUB_FIRESTORE_PROJECT  GCP project of the Firestore database; unset means in-memory
//	                       storage (nothing survives a restart — -dev only)
//	HUB_FIRESTORE_DATABASE Firestore database id; default "(default)"
//	HUB_DEV_TOKEN          phase-0 shared secret, required until pane sign-in (unit 2):
//	                       the token in a bridge hello. No longer accepted on /<c>/mcp.
//	                       Never logged.
//	HUB_ALLOWED_ORIGINS    comma-separated extra browser origins allowed on /<c>/bridge
//	HUB_ENV                "prod", "staging" or "dev" (default; -dev always forces "dev"
//	                       regardless of this variable); see hub.Options.Environment
package main

import (
	"context"
	"crypto/tls"
	"errors"
	"flag"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"runtime/debug"
	"strings"
	"syscall"
	"time"

	excel "github.com/eichler-ai/connectors/excel/connector"
	"github.com/eichler-ai/connectors/hub"
	"github.com/eichler-ai/connectors/hub/internal/authserver"
	"github.com/eichler-ai/connectors/hub/internal/devcert"
	"github.com/eichler-ai/connectors/hub/internal/store"
	"github.com/eichler-ai/connectors/internal/auth"
)

// buildVersion is set with -ldflags "-X main.buildVersion=<rev>" by the Cloud
// Build pipeline (hub/Dockerfile's VERSION build arg), which builds from a bare
// source copy with no .git directory, so the VCS stamp below is unavailable.
// Local builds leave it empty and fall back to the VCS stamp.
var buildVersion string

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "hub:", err)
		os.Exit(1)
	}
}

func run() error {
	// Admin subcommands come before flags so `hub revoke-user ID` reads
	// naturally; they share the store configuration with the server.
	if len(os.Args) > 1 && os.Args[1] == "revoke-user" {
		return revokeUser(os.Args[2:])
	}
	dev := flag.Bool("dev", false, "serve HTTPS on localhost:8443 with a self-signed certificate")
	trust := flag.Bool("trust-cert", false, "print the command that trusts the -dev certificate and exit")
	flag.Parse()
	if *trust {
		return trustCert()
	}

	// JSON to stderr is what Cloud Logging parses; text is easier to read in a
	// terminal. Neither ever carries a token, script or result.
	var handler slog.Handler
	if *dev {
		handler = slog.NewTextHandler(os.Stderr, nil)
	} else {
		handler = slog.NewJSONHandler(os.Stderr, nil)
	}
	logger := slog.New(handler)
	slog.SetDefault(logger)

	// TrimSpace: Secret Manager values written via `gcloud secrets versions add
	// --data-file=-` from a shell pipeline often carry a trailing newline, which
	// would otherwise become part of the token and never match a bearer header.
	devToken := strings.TrimSpace(os.Getenv("HUB_DEV_TOKEN"))
	if devToken == "" {
		return errors.New("HUB_DEV_TOKEN is required (bridge hello token until pane sign-in lands); set it to a random string of 16+ characters")
	}
	bridgeAuth, err := auth.NewDevToken(devToken)
	if err != nil {
		return err
	}

	port := os.Getenv("PORT")
	if port == "" {
		if *dev {
			port = "8443"
		} else {
			port = "8080"
		}
	}
	publicURL := os.Getenv("HUB_PUBLIC_URL")
	if publicURL == "" {
		publicURL = hub.DevPublicURL
		if *dev {
			// A -dev hub on another port is still its own public origin.
			publicURL = "https://localhost:" + port
		}
	}
	var origins []string
	if v := os.Getenv("HUB_ALLOWED_ORIGINS"); v != "" {
		origins = strings.Split(v, ",")
	}

	environment := strings.TrimSpace(os.Getenv("HUB_ENV"))
	if environment == "" {
		environment = "dev"
	}
	if *dev {
		// A -dev hub is always "dev" regardless of what's in the environment,
		// since it's never the production or staging deployment.
		environment = "dev"
	}

	keys, err := loadSigningKeys(*dev)
	if err != nil {
		return err
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	st, err := openStore(ctx, logger)
	if err != nil {
		return err
	}
	defer st.Close()

	var provider *authserver.OIDCProvider
	if id := os.Getenv("HUB_MS_CLIENT_ID"); id != "" {
		secret := strings.TrimSpace(os.Getenv("HUB_MS_CLIENT_SECRET"))
		if secret == "" {
			return errors.New("HUB_MS_CLIENT_SECRET is required with HUB_MS_CLIENT_ID")
		}
		provider = &authserver.OIDCProvider{Name: "microsoft", DiscoveryURL: authserver.MicrosoftDiscoveryURL, ClientID: id, ClientSecret: secret}
	} else {
		// Every OAuth endpoint still works (metadata, registration, token
		// refresh) but nobody can sign in; loud at startup, not at the
		// first user's attempt.
		logger.Warn("HUB_MS_CLIENT_ID is not set: Microsoft sign-in is disabled")
	}
	as, err := authserver.New(authserver.Options{
		Issuer:            publicURL,
		Store:             st,
		Keys:              keys,
		Connectors:        []string{excel.Slug},
		Provider:          provider,
		Logger:            logger,
		AllowLoopbackCIMD: *dev,
		TrustForwardedFor: !*dev,
	})
	if err != nil {
		return err
	}
	go as.RunCollector(ctx)

	srv, err := hub.NewServer(hub.Options{
		PublicURL:      publicURL,
		Auth:           auth.Split{Access: as.Verifier(), Bridge: bridgeAuth},
		AuthServer:     as,
		AllowedOrigins: origins,
		Connectors:     []hub.Connector{excel.New()},
		Version:        version(),
		Environment:    environment,
		Logger:         logger,
	})
	if err != nil {
		return err
	}

	httpSrv := &http.Server{
		Addr:    ":" + port,
		Handler: srv.Handler(),
		// Bridge sockets and MCP streams are long-lived, so no write timeout;
		// the header timeout alone bounds a client that never sends a request.
		ReadHeaderTimeout: 10 * time.Second,
	}

	errc := make(chan error, 1)
	go func() {
		if *dev {
			cert, certPath, err := devcert.LoadOrCreate(dataDir())
			if err != nil {
				errc <- fmt.Errorf("certificate: %w", err)
				return
			}
			httpSrv.TLSConfig = &tls.Config{Certificates: []tls.Certificate{cert}, MinVersion: tls.VersionTLS12}
			logger.Info("hub listening", "addr", "https://localhost:"+port, "public_url", publicURL, "cert", certPath, "version", version())
			logger.Info("excel add-in", "manifest", publicURL+"/excel/manifest.xml", "pane", publicURL+"/excel/addin/taskpane.html", "mcp", publicURL+"/excel/mcp")
			logger.Info("authorization server", "metadata", publicURL+"/.well-known/oauth-authorization-server", "kid", keys.KID())
			errc <- httpSrv.ListenAndServeTLS("", "")
			return
		}
		logger.Info("hub listening", "addr", "http://"+httpSrv.Addr, "public_url", publicURL, "version", version())
		errc <- httpSrv.ListenAndServe()
	}()

	select {
	case err := <-errc:
		return err
	case <-ctx.Done():
		logger.Info("hub shutting down")
		shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		return httpSrv.Shutdown(shutdownCtx)
	}
}

// loadSigningKeys reads HUB_JWT_SIGNING_KEY, or in -dev falls back to a key
// file beside the certificate, creating it the first time so tokens
// survive a restart of a dev hub.
func loadSigningKeys(dev bool) (*auth.KeySet, error) {
	if pemData := os.Getenv("HUB_JWT_SIGNING_KEY"); strings.TrimSpace(pemData) != "" {
		return auth.ParseKeySet([]byte(pemData))
	}
	if !dev {
		return nil, errors.New("HUB_JWT_SIGNING_KEY is required (PEM, P-256); deploy.sh creates it in Secret Manager")
	}
	path := filepath.Join(dataDir(), "jwt-signing-key.pem")
	pemData, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		pemData, err = auth.GenerateKeyPEM()
		if err != nil {
			return nil, err
		}
		if err := os.MkdirAll(dataDir(), 0o700); err != nil {
			return nil, err
		}
		if err := os.WriteFile(path, pemData, 0o600); err != nil {
			return nil, err
		}
		slog.Info("created -dev JWT signing key", "path", path)
	} else if err != nil {
		return nil, err
	}
	return auth.ParseKeySet(pemData)
}

// openStore picks Firestore when HUB_FIRESTORE_PROJECT is set, memory
// otherwise.
func openStore(ctx context.Context, logger *slog.Logger) (store.Store, error) {
	project := os.Getenv("HUB_FIRESTORE_PROJECT")
	if project == "" {
		logger.Warn("HUB_FIRESTORE_PROJECT is not set: using in-memory storage; users, clients and tokens vanish on restart")
		return store.NewMemory(), nil
	}
	database := os.Getenv("HUB_FIRESTORE_DATABASE")
	fs, err := store.NewFirestore(ctx, project, database)
	if err != nil {
		return nil, err
	}
	logger.Info("firestore store", "project", project, "database", database)
	return fs, nil
}

// revokeUser is the `hub revoke-user <user_id>` kill switch (PRD §13).
func revokeUser(args []string) error {
	if len(args) != 1 || args[0] == "" {
		return errors.New("usage: hub revoke-user <user_id>")
	}
	ctx := context.Background()
	st, err := openStore(ctx, slog.Default())
	if err != nil {
		return err
	}
	defer st.Close()
	n, err := st.RevokeUser(ctx, args[0])
	if err != nil {
		return err
	}
	fmt.Printf("revoked %d refresh token(s) for %s; outstanding access tokens expire within %s\n", n, args[0], auth.AccessTokenTTL)
	return nil
}

// dataDir follows the repo convention Connectors/<App>/ under the platform
// app-data root; the hub's only local state is the -dev certificate and
// the -dev JWT signing key.
func dataDir() string {
	base, err := os.UserConfigDir()
	if err != nil {
		base = os.TempDir()
	}
	return filepath.Join(base, "Connectors", "Hub")
}

// version is the VCS revision the toolchain stamped into the binary, so
// get_skills can say which build served it. buildVersion (set at link time,
// see above) wins when present, since a Cloud Build image has no VCS stamp.
func version() string {
	if buildVersion != "" {
		return buildVersion
	}
	if bi, ok := debug.ReadBuildInfo(); ok {
		var rev, modified string
		for _, s := range bi.Settings {
			switch s.Key {
			case "vcs.revision":
				rev = s.Value
			case "vcs.modified":
				if s.Value == "true" {
					modified = "-dirty"
				}
			}
		}
		if len(rev) >= 12 {
			return rev[:12] + modified
		}
	}
	return "unknown"
}

func trustCert() error {
	certPath := filepath.Join(dataDir(), "localhost.crt")
	if _, err := os.Stat(certPath); err != nil {
		return fmt.Errorf("no certificate yet: run `hub -dev` once first (%w)", err)
	}
	fmt.Printf(`The browser must trust the hub's self-signed certificate before Excel for the web will load the
add-in or open its WebSocket. On macOS, run (it prompts for your login password):

  security add-trusted-cert -r trustRoot -k ~/Library/Keychains/login.keychain-db %q

Then restart the browser and check https://localhost:8443/excel/addin/taskpane.html loads without a warning.
To remove later:  security remove-trusted-cert %q
`, certPath, certPath)
	return nil
}
