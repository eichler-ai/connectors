// excel-bridge is the Excel connector proof of concept: an HTTPS host for the task-pane add-in, a
// WebSocket bridge the add-in dials into, and a CLI that pushes a script through it.
//
//	excel-bridge serve              start the bridge on https://localhost:3000
//	excel-bridge run script.js      run a script file against the connected workbook
//	excel-bridge run -e 'return 1'  run an inline script
//	excel-bridge status             is an add-in connected, and to which workbook
//	excel-bridge trust-cert         print the command that trusts the generated certificate
package main

import (
	"bytes"
	"crypto/tls"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"io/fs"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	poc "github.com/eichler-ai/connectors/excel/poc"
	"github.com/eichler-ai/connectors/excel/poc/internal/bridge"
)

const defaultAddr = "localhost:3000"

func main() {
	log.SetFlags(log.Ltime | log.Lmicroseconds)
	if len(os.Args) < 2 {
		usage()
	}
	var err error
	switch os.Args[1] {
	case "serve":
		err = serve(os.Args[2:])
	case "run":
		err = run(os.Args[2:])
	case "status":
		err = status(os.Args[2:])
	case "trust-cert":
		err = trustCert()
	case "-h", "--help", "help":
		usage()
	default:
		fmt.Fprintf(os.Stderr, "unknown command %q\n", os.Args[1])
		usage()
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "error:", err)
		os.Exit(1)
	}
}

func usage() {
	fmt.Fprintln(os.Stderr, "usage: excel-bridge serve|run|status|trust-cert [flags]  (-h on a subcommand for its flags)")
	os.Exit(2)
}

// dataDir follows the repo convention Connectors/<App>/ under the platform app-data root.
func dataDir() string {
	base, err := os.UserConfigDir()
	if err != nil {
		base = os.TempDir()
	}
	return filepath.Join(base, "Connectors", "Excel")
}

func serve(args []string) error {
	fs_ := flag.NewFlagSet("serve", flag.ExitOnError)
	addr := fs_.String("addr", defaultAddr, "listen address; the manifest and add-in expect localhost:3000")
	addinDir := fs_.String("addin", "", "serve add-in files from this directory instead of the embedded copy")
	origins := fs_.String("origins", "", "comma-separated extra browser origins allowed on /ws (e.g. Script Lab's runner)")
	plain := fs_.Bool("plain", os.Getenv("PORT") != "", "plain HTTP (behind a TLS-terminating proxy such as Cloud Run); default on when $PORT is set")
	prefix := fs_.String("prefix", os.Getenv("BRIDGE_PREFIX"), "URL path prefix to serve under, e.g. /excel ($BRIDGE_PREFIX)")
	publicURL := fs_.String("public-url", os.Getenv("BRIDGE_PUBLIC_URL"), "external base URL substituted into the manifest, e.g. https://mcp.example.com/excel ($BRIDGE_PUBLIC_URL)")
	token := fs_.String("token", os.Getenv("BRIDGE_TOKEN"), "bearer token required on /exec and /status ($BRIDGE_TOKEN); mandatory with -plain")
	fs_.Parse(args)
	if port := os.Getenv("PORT"); port != "" && *addr == defaultAddr {
		*addr = ":" + port
	}
	if *plain && *token == "" {
		return errors.New("-plain (or $PORT) requires -token/$BRIDGE_TOKEN: the exec endpoint would be open to the network")
	}

	var addin fs.FS
	var err error
	if *addinDir != "" {
		addin = os.DirFS(*addinDir)
	} else if addin, err = fs.Sub(poc.Addin, "addin"); err != nil {
		return err
	}
	hub := bridge.NewHub()
	var handler http.Handler = bridge.Handler(hub, addin, bridge.Options{
		ExtraOrigins: strings.FieldsFunc(*origins, func(r rune) bool { return r == ',' }),
		Token:        *token,
		PublicURL:    *publicURL,
	})
	if p := strings.TrimSuffix(*prefix, "/"); p != "" {
		inner := handler
		mux := http.NewServeMux()
		mux.Handle(p+"/", http.StripPrefix(p, inner))
		mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, r *http.Request) { w.Write([]byte("ok")) })
		handler = mux
	}
	srv := &http.Server{Addr: *addr, Handler: handler}

	if *plain {
		log.Printf("bridge listening on http://%s prefix=%q public=%q (TLS terminated upstream)", *addr, *prefix, *publicURL)
		return srv.ListenAndServe()
	}
	cert, certPath, err := bridge.LoadOrCreateCert(dataDir())
	if err != nil {
		return fmt.Errorf("certificate: %w", err)
	}
	srv.TLSConfig = &tls.Config{Certificates: []tls.Certificate{cert}, MinVersion: tls.VersionTLS12}
	log.Printf("bridge listening on https://%s (cert %s)", *addr, certPath)
	log.Printf("add-in: https://%s%s/taskpane.html  manifest: https://%s%s/manifest.xml", *addr, *prefix, *addr, *prefix)
	return srv.ListenAndServeTLS("", "")
}

// bridgeURL resolves where the CLI talks to: -url, else $BRIDGE_URL, else the local default.
func bridgeURL(flagVal string) string {
	if flagVal != "" {
		return strings.TrimSuffix(flagVal, "/")
	}
	if v := os.Getenv("BRIDGE_URL"); v != "" {
		return strings.TrimSuffix(v, "/")
	}
	return "https://" + defaultAddr
}

// client returns an HTTP client for base. For the local bridge it trusts exactly the generated
// certificate; for anything else the system roots apply. Token, if set, is sent as a bearer.
func client(base, token string) (*http.Client, error) {
	tr := &http.Transport{}
	if strings.HasPrefix(base, "https://localhost") {
		pemBytes, err := os.ReadFile(filepath.Join(dataDir(), "localhost.crt"))
		if err != nil {
			return nil, fmt.Errorf("bridge certificate not found (is `excel-bridge serve` running?): %w", err)
		}
		pool := x509.NewCertPool()
		if !pool.AppendCertsFromPEM(pemBytes) {
			return nil, errors.New("bridge certificate is not valid PEM")
		}
		tr.TLSClientConfig = &tls.Config{RootCAs: pool}
	}
	return &http.Client{Transport: &authTransport{base: tr, token: token}}, nil
}

type authTransport struct {
	base  http.RoundTripper
	token string
}

func (a *authTransport) RoundTrip(r *http.Request) (*http.Response, error) {
	if a.token != "" {
		r.Header.Set("Authorization", "Bearer "+a.token)
	}
	return a.base.RoundTrip(r)
}

func run(args []string) error {
	fs_ := flag.NewFlagSet("run", flag.ExitOnError)
	url := fs_.String("url", "", "bridge base URL ($BRIDGE_URL; default https://localhost:3000)")
	token := fs_.String("token", os.Getenv("BRIDGE_TOKEN"), "bearer token for a hosted bridge ($BRIDGE_TOKEN)")
	inline := fs_.String("e", "", "inline script body (the body of an async function receiving `context`)")
	timeout := fs_.Duration("timeout", 30*time.Second, "how long to wait for the add-in's reply")
	raw := fs_.Bool("raw", false, "print the full bridge response instead of just the result")
	outPath := fs_.String("out", "", "write the result to this file: a JSON string is written unquoted, {\"base64\":...} is decoded, anything else as JSON")
	fs_.Parse(args)

	var script string
	switch {
	case *inline != "":
		script = *inline
	case fs_.NArg() == 1 && fs_.Arg(0) == "-":
		b, err := io.ReadAll(os.Stdin)
		if err != nil {
			return err
		}
		script = string(b)
	case fs_.NArg() == 1:
		b, err := os.ReadFile(fs_.Arg(0))
		if err != nil {
			return err
		}
		script = string(b)
	default:
		return errors.New("run: give a script file, `-` for stdin, or -e 'script'")
	}

	base := bridgeURL(*url)
	c, err := client(base, *token)
	if err != nil {
		return err
	}
	c.Timeout = *timeout + 5*time.Second
	body, _ := json.Marshal(bridge.ExecRequest{Script: script, TimeoutMs: timeout.Milliseconds()})
	start := time.Now()
	resp, err := c.Post(base+"/exec", "application/json", bytes.NewReader(body))
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	out, _ := io.ReadAll(resp.Body)
	var res bridge.ExecResult
	if err := json.Unmarshal(out, &res); err != nil {
		return fmt.Errorf("bridge returned HTTP %d: %s", resp.StatusCode, strings.TrimSpace(string(out)))
	}

	if *raw {
		os.Stdout.Write(out)
		return nil
	}
	if res.BridgeError != "" {
		return errors.New(res.BridgeError)
	}
	if !res.OK {
		e := res.Error
		fmt.Fprintf(os.Stderr, "script error: %s: %s\n", e.Name, e.Message)
		if e.Code != "" {
			fmt.Fprintf(os.Stderr, "  code: %s\n", e.Code)
		}
		if len(e.DebugInfo) > 0 {
			fmt.Fprintf(os.Stderr, "  debugInfo: %s\n", e.DebugInfo)
		}
		if e.Stack != "" {
			fmt.Fprintf(os.Stderr, "  stack: %s\n", indent(e.Stack))
		}
		os.Exit(3)
	}
	if *outPath != "" {
		data, err := resultBytes(res.Result)
		if err != nil {
			return err
		}
		if err := os.WriteFile(*outPath, data, 0o644); err != nil {
			return err
		}
		fmt.Fprintf(os.Stderr, "wrote %d bytes to %s\n", len(data), *outPath)
	} else if len(res.Result) > 0 && string(res.Result) != "null" {
		var pretty bytes.Buffer
		if json.Indent(&pretty, res.Result, "", "  ") == nil {
			pretty.WriteTo(os.Stdout)
		} else {
			os.Stdout.Write(res.Result)
		}
		fmt.Println()
	}
	fmt.Fprintf(os.Stderr, "[%s: addin %.0fms, roundtrip %s%s]\n", res.ID, res.DurationMs,
		time.Since(start).Round(time.Millisecond), map[bool]string{true: ", result truncated", false: ""}[res.Truncated])
	return nil
}

// resultBytes turns a script result into file contents: a bare string as-is, {"base64": "..."} decoded,
// anything else as the JSON text.
func resultBytes(raw json.RawMessage) ([]byte, error) {
	var str string
	if json.Unmarshal(raw, &str) == nil {
		return []byte(str), nil
	}
	var b64 struct {
		Base64 *string `json:"base64"`
	}
	if json.Unmarshal(raw, &b64) == nil && b64.Base64 != nil {
		return base64.StdEncoding.DecodeString(*b64.Base64)
	}
	return raw, nil
}

func indent(s string) string { return strings.ReplaceAll(s, "\n", "\n         ") }

func status(args []string) error {
	fs_ := flag.NewFlagSet("status", flag.ExitOnError)
	url := fs_.String("url", "", "bridge base URL ($BRIDGE_URL; default https://localhost:3000)")
	token := fs_.String("token", os.Getenv("BRIDGE_TOKEN"), "bearer token for a hosted bridge ($BRIDGE_TOKEN)")
	fs_.Parse(args)
	base := bridgeURL(*url)
	c, err := client(base, *token)
	if err != nil {
		return err
	}
	c.Timeout = 5 * time.Second
	resp, err := c.Get(base + "/status")
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	_, err = io.Copy(os.Stdout, resp.Body)
	return err
}

func trustCert() error {
	certPath := filepath.Join(dataDir(), "localhost.crt")
	if _, err := os.Stat(certPath); err != nil {
		return fmt.Errorf("no certificate yet: run `excel-bridge serve` once first (%w)", err)
	}
	fmt.Printf(`Chrome must trust the bridge's self-signed certificate before Excel for the web will load the
add-in or open its WebSocket. On macOS, run (it prompts for your login password):

  security add-trusted-cert -r trustRoot -k ~/Library/Keychains/login.keychain-db %q

Then restart Chrome and check https://localhost:3000/taskpane.html shows no warning.
To remove later:  security remove-trusted-cert %q
`, certPath, certPath)
	return nil
}
