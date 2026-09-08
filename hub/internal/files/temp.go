package files

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

// Temp is the Store for -dev and tests: bytes land under a local temp
// directory and SignedURL is served by the hub itself, since there is no
// Cloud Storage to point at. HUB_FILES_BUCKET unset is what selects it in
// cmd/hub — it must never be what a staging or prod deploy runs, so there is
// no env override to force it on.
//
// A signed URL here is "<publicURL>/files/local/<token>": Handler is the
// http.Handler hub.Server mounts at that path when the configured Store
// implements it (a GCS Store's signed URLs point straight at Cloud Storage
// and need no hub route at all).
type Temp struct {
	dir       string
	publicURL string

	mu     sync.Mutex
	tokens map[string]tempGrant
}

type tempGrant struct {
	path    string
	expires time.Time
}

// NewTemp creates a fresh temp directory (removed by the OS eventually;
// nothing in -dev or a test run needs it cleaned up sooner) and builds a
// Temp whose signed URLs are rooted at publicURL.
func NewTemp(publicURL string) (*Temp, error) {
	dir, err := os.MkdirTemp("", "hub-files-")
	if err != nil {
		return nil, fmt.Errorf("files: temp dir: %w", err)
	}
	return &Temp{dir: dir, publicURL: strings.TrimSuffix(publicURL, "/"), tokens: map[string]tempGrant{}}, nil
}

func (t *Temp) Put(ctx context.Context, userID, connector, id, ext string, r io.Reader) (ObjectRef, error) {
	key := fmt.Sprintf("%s/%s/%s.%s", userID, connector, id, ext)
	full := filepath.Join(t.dir, filepath.FromSlash(key))
	if err := os.MkdirAll(filepath.Dir(full), 0o700); err != nil {
		return ObjectRef{}, fmt.Errorf("files: mkdir: %w", err)
	}
	f, err := os.Create(full)
	if err != nil {
		return ObjectRef{}, fmt.Errorf("files: create: %w", err)
	}
	defer f.Close()
	n, err := io.Copy(f, r)
	if err != nil {
		return ObjectRef{}, fmt.Errorf("files: write: %w", err)
	}
	return ObjectRef{Key: key, Bytes: n}, nil
}

func (t *Temp) SignedURL(ctx context.Context, ref ObjectRef, ttl time.Duration) (string, time.Time, error) {
	tok, err := randomToken()
	if err != nil {
		return "", time.Time{}, err
	}
	expires := time.Now().Add(ttl)
	t.mu.Lock()
	t.tokens[tok] = tempGrant{path: filepath.Join(t.dir, filepath.FromSlash(ref.Key)), expires: expires}
	t.mu.Unlock()
	return t.publicURL + "/files/local/" + tok, expires, nil
}

// Handler serves the URLs SignedURL mints. Mounted at GET /files/local/ —
// see hub.Server. A token is single-use-until-expiry (readable more than
// once within ttl, matching a real signed URL a client might retry against).
func (t *Temp) Handler() http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		tok := strings.TrimPrefix(r.URL.Path, "/files/local/")
		t.mu.Lock()
		grant, ok := t.tokens[tok]
		t.mu.Unlock()
		if !ok || time.Now().After(grant.expires) {
			http.Error(w, "expired or unknown link", http.StatusGone)
			return
		}
		http.ServeFile(w, r, grant.path)
	})
}

func randomToken() (string, error) {
	b := make([]byte, 20)
	if _, err := rand.Read(b); err != nil {
		return "", fmt.Errorf("files: random token: %w", err)
	}
	return hex.EncodeToString(b), nil
}
