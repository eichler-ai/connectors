// hub is the Connectors Hub server (hub/docs/PRD.md): one process serving every
// connector's MCP endpoint, bridge socket and extension files.
//
//	hub                  serve plain HTTP on $PORT (Cloud Run; TLS terminated upstream)
//	hub -dev             serve HTTPS on https://localhost:8443 with a self-signed certificate,
//	                     so the add-in can be sideloaded against this laptop
//	hub -trust-cert      print the command that trusts the -dev certificate
//
// Environment:
//
//	PORT                 listen port (Cloud Run sets it); default 8080, or 8443 with -dev
//	HUB_PUBLIC_URL       externally visible origin, e.g. https://connectors.eichler.ai;
//	                     default https://localhost:8443 (rewritten into served manifests)
//	HUB_DEV_TOKEN        phase-0 shared secret, required: bearer token on /<c>/mcp and the
//	                     token in a bridge hello. Never logged.
//	HUB_ALLOWED_ORIGINS  comma-separated extra browser origins allowed on /<c>/bridge
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
	"github.com/eichler-ai/connectors/hub/internal/devcert"
	"github.com/eichler-ai/connectors/internal/auth"
)

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "hub:", err)
		os.Exit(1)
	}
}

func run() error {
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

	devToken := os.Getenv("HUB_DEV_TOKEN")
	if devToken == "" {
		return errors.New("HUB_DEV_TOKEN is required (phase-0 auth); set it to a random string of 16+ characters")
	}
	authn, err := auth.NewDevToken(devToken)
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

	srv, err := hub.NewServer(hub.Options{
		PublicURL:      publicURL,
		Auth:           authn,
		AllowedOrigins: origins,
		Connectors:     []hub.Connector{excel.New()},
		Version:        version(),
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

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
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

// dataDir follows the repo convention Connectors/<App>/ under the platform
// app-data root; the hub's only local state is the -dev certificate.
func dataDir() string {
	base, err := os.UserConfigDir()
	if err != nil {
		base = os.TempDir()
	}
	return filepath.Join(base, "Connectors", "Hub")
}

// version is the VCS revision the toolchain stamped into the binary, so
// get_skills can say which build served it.
func version() string {
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
