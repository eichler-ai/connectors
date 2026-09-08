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
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
)

// Client ID Metadata Documents (draft-ietf-oauth-client-id-metadata-document
// -00, adopted by the MCP authorization spec 2025-11-25): the client_id is an
// https URL whose JSON body is the registration. The authorization server
// fetches it, checks `client_id` inside equals the URL, and uses its
// redirect_uris exactly as it would a dynamic registration.

const (
	cimdFetchTimeout = 5 * time.Second
	// cimdMaxBytes: the draft recommends 5 kB; Claude's documents are a few
	// hundred bytes. 16 kB leaves room for logo and policy URLs.
	cimdMaxBytes = 16 << 10
	// Cache TTL honours Cache-Control max-age inside these bounds (draft
	// §4.4 lets the server set its own), default when absent.
	cimdMinTTL     = 5 * time.Minute
	cimdMaxTTL     = 24 * time.Hour
	cimdDefaultTTL = time.Hour
	// cimdMaxEntries bounds the cache; on overflow expired entries go first,
	// then the soonest-expiring.
	cimdMaxEntries = 1000
)

type cimdDoc struct {
	ClientID                string   `json:"client_id"`
	ClientName              string   `json:"client_name"`
	RedirectURIs            []string `json:"redirect_uris"`
	TokenEndpointAuthMethod string   `json:"token_endpoint_auth_method"`
}

type cimdEntry struct {
	client  client
	expires time.Time
}

type cimdCache struct {
	http *http.Client
	now  func() time.Time
	mu   sync.Mutex
	docs map[string]cimdEntry
}

func newCIMDCache(hc *http.Client, allowLoopback bool, now func() time.Time) *cimdCache {
	if hc == nil {
		hc = &http.Client{Transport: &http.Transport{
			DialContext: (&net.Dialer{Timeout: cimdFetchTimeout, Control: guardDial(allowLoopback)}).DialContext,
			// The dialer's Control sees the resolved address, so a DNS name
			// that points at a private range is refused (SSRF, draft §6.5).
			Proxy:               nil,
			TLSHandshakeTimeout: cimdFetchTimeout,
		}}
	}
	// Copy before adjusting: an injected client belongs to the caller.
	own := *hc
	hc = &own
	// No redirects: a document must live at its own URL, and a redirect
	// would let a public host forward the fetch somewhere it should not go.
	hc.CheckRedirect = func(*http.Request, []*http.Request) error { return errors.New("redirects are not followed") }
	hc.Timeout = cimdFetchTimeout
	return &cimdCache{http: hc, now: now, docs: map[string]cimdEntry{}}
}

// guardDial refuses connections to private, loopback, link-local and
// unspecified addresses unless loopback is explicitly allowed (tests, -dev).
func guardDial(allowLoopback bool) func(network, address string, c syscall.RawConn) error {
	return func(network, address string, _ syscall.RawConn) error {
		host, _, err := net.SplitHostPort(address)
		if err != nil {
			return err
		}
		ip := net.ParseIP(host)
		if ip == nil {
			return fmt.Errorf("cimd: unexpected dial address %q", address)
		}
		if ip.IsLoopback() && allowLoopback {
			return nil
		}
		if ip.IsLoopback() || ip.IsPrivate() || ip.IsLinkLocalUnicast() || ip.IsLinkLocalMulticast() || ip.IsUnspecified() || ip.IsMulticast() {
			return fmt.Errorf("cimd: refusing to fetch from non-public address %s", ip)
		}
		return nil
	}
}

// validClientIDURL applies draft §3: https, a path, no dot segments, no
// fragment, no userinfo. The query string is discouraged but not forbidden.
func validClientIDURL(raw string) (*url.URL, error) {
	u, err := url.Parse(raw)
	if err != nil || u.Scheme != "https" || u.Host == "" {
		return nil, errors.New("client_id must be an https URL")
	}
	if u.Path == "" || u.Path == "/" {
		return nil, errors.New("client_id URL must contain a path component")
	}
	for _, seg := range strings.Split(u.Path, "/") {
		if seg == "." || seg == ".." {
			return nil, errors.New("client_id URL must not contain dot path segments")
		}
	}
	if u.Fragment != "" || u.User != nil || strings.Contains(raw, "#") {
		return nil, errors.New("client_id URL must not contain a fragment or credentials")
	}
	if ip := net.ParseIP(u.Hostname()); ip != nil && !ip.IsLoopback() {
		return nil, errors.New("client_id URL must use a host name")
	}
	return u, nil
}

func (c *cimdCache) resolve(ctx context.Context, id string) (client, error) {
	if _, err := validClientIDURL(id); err != nil {
		return client{}, err
	}
	now := c.now()
	c.mu.Lock()
	e, ok := c.docs[id]
	c.mu.Unlock()
	if ok && e.expires.After(now) {
		return e.client, nil
	}
	cl, ttl, err := c.fetch(ctx, id)
	if err != nil {
		// Errors and invalid documents are never cached (draft §4.4).
		return client{}, err
	}
	c.mu.Lock()
	c.evictLocked(now)
	c.docs[id] = cimdEntry{client: cl, expires: now.Add(ttl)}
	c.mu.Unlock()
	return cl, nil
}

func (c *cimdCache) evictLocked(now time.Time) {
	if len(c.docs) < cimdMaxEntries {
		return
	}
	for k, e := range c.docs {
		if !e.expires.After(now) {
			delete(c.docs, k)
		}
	}
	for len(c.docs) >= cimdMaxEntries {
		var soonest string
		var when time.Time
		for k, e := range c.docs {
			if soonest == "" || e.expires.Before(when) {
				soonest, when = k, e.expires
			}
		}
		delete(c.docs, soonest)
	}
}

func (c *cimdCache) fetch(ctx context.Context, id string) (client, time.Duration, error) {
	ctx, cancel := context.WithTimeout(ctx, cimdFetchTimeout)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, id, nil)
	if err != nil {
		return client{}, 0, err
	}
	req.Header.Set("Accept", "application/json")
	req.Header.Set("User-Agent", "connectors-hub-authserver")
	resp, err := c.http.Do(req)
	if err != nil {
		return client{}, 0, fmt.Errorf("client metadata document: fetch failed: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return client{}, 0, fmt.Errorf("client metadata document: HTTP %d", resp.StatusCode)
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, cimdMaxBytes+1))
	if err != nil {
		return client{}, 0, fmt.Errorf("client metadata document: read: %w", err)
	}
	if len(body) > cimdMaxBytes {
		return client{}, 0, fmt.Errorf("client metadata document exceeds %d bytes", cimdMaxBytes)
	}
	var doc cimdDoc
	if err := json.Unmarshal(body, &doc); err != nil {
		return client{}, 0, errors.New("client metadata document is not a JSON object")
	}
	if doc.ClientID != id {
		return client{}, 0, errors.New("client metadata document's client_id does not match its URL")
	}
	if len(doc.RedirectURIs) == 0 || len(doc.RedirectURIs) > maxRedirectURIs {
		return client{}, 0, errors.New("client metadata document must list 1 to 10 redirect_uris")
	}
	for _, u := range doc.RedirectURIs {
		if err := validRedirectURI(u); err != nil {
			return client{}, 0, fmt.Errorf("client metadata document: %w", err)
		}
	}
	if m := doc.TokenEndpointAuthMethod; m != "" && m != "none" {
		// Public clients only (draft §4.1 forbids shared-secret methods;
		// private_key_jwt is not something a Claude client uses).
		return client{}, 0, fmt.Errorf("client metadata document: token_endpoint_auth_method %q is not supported", m)
	}
	name := strings.TrimSpace(doc.ClientName)
	if name == "" {
		name = id
	}
	if len(name) > maxClientName {
		name = name[:maxClientName]
	}
	return client{ID: id, Name: name, RedirectURIs: doc.RedirectURIs}, cacheTTL(resp.Header.Get("Cache-Control")), nil
}

// cacheTTL reads max-age, clamped to the bounds above.
func cacheTTL(cacheControl string) time.Duration {
	for _, d := range strings.Split(cacheControl, ",") {
		d = strings.TrimSpace(d)
		if strings.HasPrefix(d, "max-age=") {
			if secs, err := strconv.Atoi(strings.TrimPrefix(d, "max-age=")); err == nil {
				return min(max(time.Duration(secs)*time.Second, cimdMinTTL), cimdMaxTTL)
			}
		}
	}
	return cimdDefaultTTL
}
