package mcpserver

import (
	"context"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/diag"
)

// scriptSourceName is the diag Source for every record this file produces:
// resolution happens in the MCP tool layer, before the execution manager is
// ever called, so these are reported as mcp-server.internal.mcpserver.
const scriptSourceName = "mcp-server.internal.mcpserver"

// maxScriptSourceBytes caps a script resolved from script_path. A script is
// KBs in practice; this ceiling is generous but keeps a stray large file or a
// runaway URL well under the 64 MiB NDJSON wire framing limit (transport
// package) and off the agent's shoulders entirely. A var, not a const, so
// tests can shrink it without writing multi-megabyte fixtures.
var maxScriptSourceBytes int64 = 8 << 20 // 8 MiB

// scriptFetchTimeout bounds a script_path URL fetch, independent of the
// script's own execution timeout. Resolution is a small text GET; it should
// never hold the tool call open for long.
const scriptFetchTimeout = 30 * time.Second

// utf8BOM is the byte-order mark Windows editors and PowerShell prepend to
// UTF-8 files. Left in place it becomes a leading U+FEFF in the script text
// and fails Roslyn compilation, so resolveScript strips it.
var utf8BOM = []byte{0xEF, 0xBB, 0xBF}

// resolveScript turns the two mutually-exclusive script inputs into the single
// script body to compile. Exactly one of script (inline) or scriptPath (an
// absolute local path or an https URL) must be non-empty; the file or URL is
// dereferenced here on the server host, so the bytes never travel through the
// agent's context. On any problem it returns a diag.Record describing it and
// an empty script, so the caller can surface a clean pre-execution error
// (no execution_id is minted).
func resolveScript(ctx context.Context, script, scriptPath string) (string, *diag.Record) {
	haveScript := strings.TrimSpace(script) != ""
	havePath := strings.TrimSpace(scriptPath) != ""

	switch {
	case haveScript && havePath:
		return "", diag.New(diag.SeverityError, "script-source-ambiguous", scriptSourceName,
			"both script and script_path were provided; pass exactly one").
			WithRemedy("send the C# inline as script, or a path/URL as script_path, but not both")
	case !haveScript && !havePath:
		return "", diag.New(diag.SeverityError, "script-source-required", scriptSourceName,
			"neither script nor script_path was provided").
			WithRemedy("pass the C# inline as script, or an absolute local path or https URL as script_path")
	case haveScript:
		// Inline runs verbatim (its prior behavior); only a leading BOM is
		// stripped, the same as the file/URL path, so a BOM can't reach Roslyn
		// from either input.
		return stripBOM([]byte(script)), nil
	}

	// script_path branch.
	scriptPath = strings.TrimSpace(scriptPath)
	var (
		raw []byte
		rec *diag.Record
	)
	if isHTTPRef(scriptPath) {
		raw, rec = fetchScriptURL(ctx, scriptPath)
	} else {
		raw, rec = readScriptFile(scriptPath)
	}
	if rec != nil {
		return "", rec
	}

	text := stripBOM(raw)
	if strings.TrimSpace(text) == "" {
		return "", diag.New(diag.SeverityError, "script-empty", scriptSourceName,
			fmt.Sprintf("script_path %q resolved to an empty script", scriptPath)).
			WithRemedy("point script_path at a file whose contents are the C# script body")
	}
	return text, nil
}

// isHTTPRef reports whether ref is an http(s) URL rather than a filesystem
// path. It keys off an explicit scheme prefix so a Windows drive-letter path
// like C:\scripts\x.cs is never mistaken for a URL with scheme "c".
func isHTTPRef(ref string) bool {
	lower := strings.ToLower(ref)
	return strings.HasPrefix(lower, "http://") || strings.HasPrefix(lower, "https://")
}

// readScriptFile reads an absolute local path, capped at maxScriptSourceBytes.
func readScriptFile(path string) ([]byte, *diag.Record) {
	if !filepath.IsAbs(path) {
		return nil, diag.New(diag.SeverityError, "script-path-not-absolute", scriptSourceName,
			fmt.Sprintf("script_path %q is not absolute", path)).
			WithRemedy(`pass an absolute path (e.g. C:\scripts\walls.cs on Windows, /Users/you/walls.cs on macOS)`)
	}
	// Require a regular file: a FIFO or a character device (/dev/stdin) would
	// block the read with no timeout, and a directory opens fine only to fail
	// with a confusing read error. Reject all of those up front, clearly.
	info, err := os.Stat(path)
	if err != nil {
		return nil, diag.New(diag.SeverityError, "script-file-unreadable", scriptSourceName,
			fmt.Sprintf("could not stat script_path %q: %v", path, err)).
			WithRemedy("check the path exists on the connector's host and is readable")
	}
	if !info.Mode().IsRegular() {
		return nil, diag.New(diag.SeverityError, "script-path-not-a-file", scriptSourceName,
			fmt.Sprintf("script_path %q is not a regular file", path)).
			WithRemedy("point script_path at a regular file, not a directory, pipe, or device")
	}
	f, err := os.Open(path)
	if err != nil {
		return nil, diag.New(diag.SeverityError, "script-file-unreadable", scriptSourceName,
			fmt.Sprintf("could not open script_path %q: %v", path, err)).
			WithRemedy("check the path exists on the connector's host and is readable")
	}
	defer f.Close()

	raw, rec := readCapped(f, fmt.Sprintf("script_path file %q", path))
	if rec != nil {
		return nil, rec
	}
	return raw, nil
}

// scriptHTTPClient fetches script_path URLs. Its CheckRedirect re-applies the
// scheme policy to EVERY redirect hop: http.Client follows 3xx by default, so
// validating only the initial URL would let an https URL 302 to
// http://<internal-host> (or a link-local metadata address) and defeat the
// http-only-to-loopback rule. Blocking here fails Do with the redirect error,
// so the redirected body is never read or compiled.
var scriptHTTPClient = &http.Client{CheckRedirect: checkScriptRedirect}

func checkScriptRedirect(req *http.Request, via []*http.Request) error {
	if len(via) >= 10 {
		return fmt.Errorf("stopped after %d redirects", len(via))
	}
	switch req.URL.Scheme {
	case "https":
		return nil
	case "http":
		if isLoopbackHost(req.URL.Hostname()) {
			return nil
		}
		return fmt.Errorf("redirect to http non-loopback host %q blocked", req.URL.Host)
	default:
		return fmt.Errorf("redirect to disallowed scheme %q", req.URL.Scheme)
	}
}

// fetchScriptURL GETs an http(s) URL, capped at maxScriptSourceBytes. https is
// allowed to any host; http only to loopback, so a plaintext fetch can be used
// for local development without opening a cleartext hop to an arbitrary host.
func fetchScriptURL(ctx context.Context, raw string) ([]byte, *diag.Record) {
	u, err := url.Parse(raw)
	if err != nil {
		return nil, diag.New(diag.SeverityError, "script-url-invalid", scriptSourceName,
			fmt.Sprintf("script_path %q is not a valid URL: %v", raw, err)).
			WithRemedy("pass a valid https URL")
	}
	if u.Scheme == "http" && !isLoopbackHost(u.Hostname()) {
		return nil, diag.New(diag.SeverityError, "script-url-scheme-blocked", scriptSourceName,
			fmt.Sprintf("script_path %q uses http to a non-loopback host; only https is allowed for remote URLs", raw)).
			WithRemedy("use an https URL (http is permitted only for localhost)")
	}

	reqCtx, cancel := context.WithTimeout(ctx, scriptFetchTimeout)
	defer cancel()
	req, err := http.NewRequestWithContext(reqCtx, http.MethodGet, raw, nil)
	if err != nil {
		return nil, diag.New(diag.SeverityError, "script-url-invalid", scriptSourceName,
			fmt.Sprintf("could not build request for script_path %q: %v", raw, err)).
			WithRemedy("pass a valid https URL")
	}
	resp, err := scriptHTTPClient.Do(req)
	if err != nil {
		return nil, diag.New(diag.SeverityError, "script-url-fetch-failed", scriptSourceName,
			fmt.Sprintf("fetching script_path %q failed: %v", raw, err)).
			WithRemedy("check the URL is reachable from the connector's host and does not redirect to a blocked host")
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, diag.New(diag.SeverityError, "script-url-fetch-failed", scriptSourceName,
			fmt.Sprintf("fetching script_path %q returned HTTP %d", raw, resp.StatusCode)).
			WithRemedy("check the URL serves the script body at status 200")
	}

	body, rec := readCapped(resp.Body, fmt.Sprintf("script_path URL %q", raw))
	if rec != nil {
		return nil, rec
	}
	return body, nil
}

// readCapped reads r fully but refuses anything larger than
// maxScriptSourceBytes, reading one byte past the cap to detect the overflow.
func readCapped(r io.Reader, what string) ([]byte, *diag.Record) {
	raw, err := io.ReadAll(io.LimitReader(r, maxScriptSourceBytes+1))
	if err != nil {
		return nil, diag.New(diag.SeverityError, "script-source-read-failed", scriptSourceName,
			fmt.Sprintf("reading %s failed: %v", what, err)).
			WithRemedy("check the source is readable and try again")
	}
	if int64(len(raw)) > maxScriptSourceBytes {
		return nil, diag.New(diag.SeverityError, "script-source-too-large", scriptSourceName,
			fmt.Sprintf("%s exceeds the %d-byte script size limit", what, maxScriptSourceBytes)).
			WithRemedy("split the work into smaller scripts, or pass the script inline if it is genuinely this large")
	}
	return raw, nil
}

// isLoopbackHost reports whether host names the local machine.
func isLoopbackHost(host string) bool {
	if strings.EqualFold(host, "localhost") {
		return true
	}
	if ip := net.ParseIP(host); ip != nil {
		return ip.IsLoopback()
	}
	return false
}

// stripBOM removes a leading UTF-8 byte-order mark, if present.
func stripBOM(b []byte) string {
	if len(b) >= len(utf8BOM) && string(b[:len(utf8BOM)]) == string(utf8BOM) {
		b = b[len(utf8BOM):]
	}
	return string(b)
}
