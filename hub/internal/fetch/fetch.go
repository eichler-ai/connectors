// Package fetch is the hub's guarded outbound HTTP GET for user-supplied
// URLs — today just import_workbook's source_url (hub PRD §10/§11, reversed:
// "an https URL the hub fetches server-side, SSRF-guarded"). It applies the
// same private-range refusal as the authorization server's Client ID
// Metadata Document fetcher (hub/internal/authserver/cimd.go), pulled out
// here so a connector's import path can share it without importing
// authserver, which is authorization-server-specific.
package fetch

import (
	"context"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"syscall"
	"time"
)

// Timeout bounds the whole fetch: dial, TLS handshake and response headers.
const Timeout = 10 * time.Second

// Bytes fetches url (https only, no redirects followed) and returns its
// body, capped at maxBytes — a response larger than that is an error, never
// silently truncated, since truncating an xlsx would just move "not a valid
// xlsx" to a more confusing place. Refuses to dial any address that resolves
// to a private, loopback, link-local, unspecified or multicast range (SSRF;
// same guard as cimd.go's).
func Bytes(ctx context.Context, rawURL string, maxBytes int64) ([]byte, error) {
	u, err := url.Parse(rawURL)
	if err != nil || u.Scheme != "https" || u.Host == "" {
		return nil, fmt.Errorf("fetch: %q is not an https URL", rawURL)
	}
	client := &http.Client{
		Timeout: Timeout,
		// A redirect could point at a private host the initial URL check
		// passed; refusing it is simpler and safer than re-validating each hop.
		CheckRedirect: func(*http.Request, []*http.Request) error { return fmt.Errorf("fetch: redirects are not followed") },
		Transport: &http.Transport{
			DialContext: (&net.Dialer{Timeout: Timeout, Control: guardDial}).DialContext,
		},
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, rawURL, nil)
	if err != nil {
		return nil, fmt.Errorf("fetch: %w", err)
	}
	req.Header.Set("User-Agent", "connectors-hub")
	resp, err := client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("fetch: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("fetch: HTTP %d", resp.StatusCode)
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, maxBytes+1))
	if err != nil {
		return nil, fmt.Errorf("fetch: read: %w", err)
	}
	if int64(len(body)) > maxBytes {
		return nil, fmt.Errorf("fetch: response exceeds %d bytes", maxBytes)
	}
	return body, nil
}

// guardDial sees the resolved address (not the hostname), so a DNS name that
// points at a private range is refused exactly like a literal private IP.
func guardDial(network, address string, _ syscall.RawConn) error {
	host, _, err := net.SplitHostPort(address)
	if err != nil {
		return err
	}
	ip := net.ParseIP(host)
	if ip == nil {
		return fmt.Errorf("fetch: unexpected dial address %q", address)
	}
	if ip.IsLoopback() || ip.IsPrivate() || ip.IsLinkLocalUnicast() || ip.IsLinkLocalMulticast() || ip.IsUnspecified() || ip.IsMulticast() {
		return fmt.Errorf("fetch: refusing to fetch from non-public address %s", ip)
	}
	return nil
}
