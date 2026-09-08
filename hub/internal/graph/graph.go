// Package graph is the hub's Microsoft Graph client (RFC
// excel/docs/rfc-graph-create-and-open.md §3.1, §3.2): given a user's
// stored Microsoft refresh token it mints a fresh Graph access token and
// puts a file in their OneDrive. It knows nothing about the hub's own store
// or encryption — hub.Host (hub/host.go) owns fetching and persisting the
// token via hub/internal/graphtoken; this package only ever sees plaintext
// tokens handed to it and returns plaintext tokens to persist.
package graph

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// TokenEndpoint is Microsoft's multi-tenant token endpoint — the same
// `common` authority authserver.MicrosoftDiscoveryURL signs users in
// through, since refresh tokens minted there are redeemed there.
const TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token"

// graphAPIBase is the default Graph endpoint.
const graphAPIBase = "https://graph.microsoft.com/v1.0"

// scope is requested on every refresh. offline_access keeps the rotated
// token able to refresh again; Files.ReadWrite is the one grant this
// package uses.
const scope = "Files.ReadWrite offline_access"

// maxSimpleUploadBytes is Microsoft Graph's ceiling for a single PUT to
// .../content; a bigger file needs an upload session (not built in this
// unit — see excel/docs/rfc-graph-create-and-open.md §3.2 "Later").
const maxSimpleUploadBytes = 4 << 20

// DriveItem is the subset of Microsoft Graph's driveItem this package reads
// (https://learn.microsoft.com/graph/api/resources/driveitem).
type DriveItem struct {
	ID              string `json:"id"`
	Name            string `json:"name"`
	WebURL          string `json:"webUrl"`
	ParentReference struct {
		DriveID string `json:"driveId"`
	} `json:"parentReference"`
}

// DriveID is the CID segment of the item's id — the part before "!" — which
// the RFC's live spike (§3.3) found is also parentReference.driveId and is
// what the pane-matching doc_key is built from: the web pane's
// Office.context.document.url for a personal-OneDrive file is
// https://d.docs.live.net/<CID>/<filename>. The id is preferred because
// it's always present; parentReference.driveId is the fallback for a
// response shape that omits it.
func (d DriveItem) DriveID() string {
	if i := strings.IndexByte(d.ID, '!'); i >= 0 {
		return d.ID[:i]
	}
	return d.ParentReference.DriveID
}

// Client talks to Microsoft Graph on behalf of a user. It mints a fresh
// access token from the caller's stored refresh token on every call — no
// access-token caching in this unit: create_workbook is not called often
// enough for the extra round trip to matter, and caching one would need its
// own store and its own expiry handling.
type Client struct {
	ClientID     string
	ClientSecret string
	// HTTPClient defaults to a 30s-timeout client; tests point it at a fake
	// Microsoft token endpoint and a fake Graph.
	HTTPClient *http.Client
	// TokenEndpoint and APIBase default to Microsoft's; overridable for tests.
	TokenEndpoint string
	APIBase       string
}

func (c *Client) httpClient() *http.Client {
	if c.HTTPClient != nil {
		return c.HTTPClient
	}
	return &http.Client{Timeout: 30 * time.Second}
}

func (c *Client) tokenEndpoint() string {
	if c.TokenEndpoint != "" {
		return c.TokenEndpoint
	}
	return TokenEndpoint
}

func (c *Client) apiBase() string {
	if c.APIBase != "" {
		return c.APIBase
	}
	return graphAPIBase
}

// refresh mints a fresh Graph access token from refreshToken. It returns the
// refresh token to persist afterwards alongside the access token: Microsoft
// rotates the refresh token on some responses and not others, so the
// returned value is refreshToken unchanged when Microsoft didn't send a new
// one — the caller always persists whatever comes back rather than deciding
// whether it changed.
func (c *Client) refresh(ctx context.Context, refreshToken string) (accessToken, nextRefreshToken string, err error) {
	form := url.Values{
		"client_id":     {c.ClientID},
		"client_secret": {c.ClientSecret},
		"grant_type":    {"refresh_token"},
		"refresh_token": {refreshToken},
		"scope":         {scope},
	}
	ctx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.tokenEndpoint(), strings.NewReader(form.Encode()))
	if err != nil {
		return "", "", err
	}
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	resp, err := c.httpClient().Do(req)
	if err != nil {
		return "", "", fmt.Errorf("graph: token refresh: %w", err)
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		return "", "", err
	}
	var tok struct {
		AccessToken  string `json:"access_token"`
		RefreshToken string `json:"refresh_token"`
		Error        string `json:"error"`
		ErrorDesc    string `json:"error_description"`
	}
	if err := json.Unmarshal(body, &tok); err != nil || resp.StatusCode != http.StatusOK || tok.AccessToken == "" {
		if tok.Error != "" {
			// Entra's descriptions carry no secret; useful in a log or an
			// error returned up to the tool caller.
			return "", "", fmt.Errorf("graph: token refresh: %s: %s", tok.Error, tok.ErrorDesc)
		}
		return "", "", fmt.Errorf("graph: token refresh: HTTP %d", resp.StatusCode)
	}
	next := tok.RefreshToken
	if next == "" {
		next = refreshToken
	}
	return tok.AccessToken, next, nil
}

// CreateFileInFolder uploads content as name inside folder (root-relative,
// e.g. "Eichler Connectors") in the user's OneDrive, minting a fresh access
// token from refreshToken first (RFC §3.1, §3.2). It always returns the
// refresh token to persist — see refresh — even when the create itself
// fails after a successful refresh, so a caller that rotated mid-call
// doesn't lose the new token.
func (c *Client) CreateFileInFolder(ctx context.Context, refreshToken, folder, name string, content []byte) (DriveItem, string, error) {
	if len(content) > maxSimpleUploadBytes {
		return DriveItem{}, refreshToken, fmt.Errorf("graph: %d bytes exceeds the %d-byte simple-upload limit; needs an upload session", len(content), maxSimpleUploadBytes)
	}
	accessToken, nextRefreshToken, err := c.refresh(ctx, refreshToken)
	if err != nil {
		return DriveItem{}, refreshToken, err
	}
	path := fmt.Sprintf("/me/drive/root:/%s/%s:/content", url.PathEscape(folder), url.PathEscape(name))
	req, err := http.NewRequestWithContext(ctx, http.MethodPut, c.apiBase()+path, bytes.NewReader(content))
	if err != nil {
		return DriveItem{}, nextRefreshToken, err
	}
	req.Header.Set("Authorization", "Bearer "+accessToken)
	req.Header.Set("Content-Type", "application/octet-stream")
	resp, err := c.httpClient().Do(req)
	if err != nil {
		return DriveItem{}, nextRefreshToken, fmt.Errorf("graph: create file: %w", err)
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		return DriveItem{}, nextRefreshToken, err
	}
	if resp.StatusCode != http.StatusOK && resp.StatusCode != http.StatusCreated {
		return DriveItem{}, nextRefreshToken, fmt.Errorf("graph: create file: HTTP %d: %s", resp.StatusCode, string(body))
	}
	var item DriveItem
	if err := json.Unmarshal(body, &item); err != nil {
		return DriveItem{}, nextRefreshToken, fmt.Errorf("graph: create file: unexpected response: %w", err)
	}
	return item, nextRefreshToken, nil
}
