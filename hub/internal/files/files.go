// Package files is the hub's file exchange (PRD §11): where an export's
// bytes land and how a client gets a short-lived URL to them. It is the seam
// hub.Host calls through; the GCS implementation (gcs.go) is what staging
// and prod run, and the Temp implementation (temp.go) is what -dev and tests
// run without a real bucket — both exercise the exact same path.
package files

import (
	"context"
	"io"
	"time"
)

// ObjectRef is what Put returns and SignedURL takes: enough for the same
// implementation to find the object again. Bytes is what Put itself counted
// while streaming, never a client-declared Content-Length — the audit line
// (PRD §12) reports this figure.
type ObjectRef struct {
	// Key is the object's path within the store: files/{user_id}/{connector}/{id}.{ext}
	// (PRD §11).
	Key   string
	Bytes int64
}

// Store is the interface hub.Host holds. Put streams — an implementation
// must never buffer a whole multi-MiB export in memory (§10) — and
// SignedURL mints a URL a client can GET directly with no further auth,
// valid until the returned expiry.
type Store interface {
	Put(ctx context.Context, userID, connector, id, ext string, r io.Reader) (ObjectRef, error)
	SignedURL(ctx context.Context, ref ObjectRef, ttl time.Duration) (url string, expiresAt time.Time, err error)
}
