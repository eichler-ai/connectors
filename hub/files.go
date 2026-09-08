package hub

import (
	"encoding/json"
	"io"
	"net/http"
	"regexp"
	"slices"
	"strings"
	"sync"
	"time"

	"golang.org/x/time/rate"

	"github.com/eichler-ai/connectors/hub/protocol"
)

// maxUploadBytes caps one export upload. Exports are whole-document
// artefacts (xlsx, pdf), not arbitrary user uploads, so 100 MiB is generous
// headroom over anything the POC measured (§03: a 200-page PDF export).
const maxUploadBytes = 100 << 20

// validFormat is deliberately narrow: Format becomes the stored object's
// file extension (files/{user}/{connector}/{id}.{ext}, §11), so it is
// checked against this shape even though bridge.Service.UploadAuthorize
// already only ever returns a format the hub itself asked for — defence in
// depth against a future connector that forgets to validate its own input.
var validFormat = regexp.MustCompile(`^[a-z0-9]{1,8}$`)

// filesUploadHandler is POST /<connector>/files?id=<export id>: the pane's
// plain-HTTPS upload of an export's bytes (§10). Auth is the same bridge
// token presented in hello, reusing Options.Auth.VerifyBridgeToken — there
// is deliberately no separate credential for this endpoint.
func (s *Server) filesUploadHandler(connector string) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		token := r.Header.Get("Authorization")
		if after, ok := strings.CutPrefix(token, "Bearer "); ok {
			token = after
		}
		principal, err := s.opts.Auth.VerifyBridgeToken(r.Context(), token)
		if err != nil {
			http.Error(w, "invalid bridge token", http.StatusUnauthorized)
			return
		}
		if len(principal.Scopes) > 0 && !slices.Contains(principal.Scopes, connector) {
			http.Error(w, "token is for another connector", http.StatusForbidden)
			return
		}
		if !s.uploads.allow(principal.UserID) {
			s.opts.Logger.Warn("files: upload rate limit", "user", principal.UserID, "connector", connector)
			http.Error(w, "too many uploads; slow down", http.StatusTooManyRequests)
			return
		}
		if r.ContentLength > maxUploadBytes {
			http.Error(w, "file too large", http.StatusRequestEntityTooLarge)
			return
		}
		id := r.URL.Query().Get("id")
		if id == "" {
			http.Error(w, "id is required", http.StatusBadRequest)
			return
		}
		format, ok := s.host.bridges.UploadAuthorize(id, principal.UserID)
		if !ok || !validFormat.MatchString(format) {
			// Unknown id (never issued), the export already timed out and
			// forgot it, or someone else's id — the brief's "a late upload
			// after the tool timed out is dropped with a log line".
			s.opts.Logger.Info("files: upload for unknown or expired export", "user", principal.UserID, "connector", connector, "id", id)
			http.Error(w, "unknown or expired export id", http.StatusNotFound)
			return
		}

		start := time.Now()
		limited := io.LimitReader(r.Body, maxUploadBytes+1)
		ref, err := s.host.files.Put(r.Context(), principal.UserID, connector, id, format, limited)
		if err != nil {
			s.opts.Logger.Error("files: put failed", "user", principal.UserID, "connector", connector, "id", id, "err", err)
			http.Error(w, "could not store the file", http.StatusInternalServerError)
			return
		}
		if ref.Bytes > maxUploadBytes {
			s.opts.Logger.Warn("files: upload exceeded the size cap", "user", principal.UserID, "connector", connector, "id", id, "bytes", ref.Bytes)
			http.Error(w, "file too large", http.StatusRequestEntityTooLarge)
			return
		}
		url, expiresAt, err := s.host.files.SignedURL(r.Context(), ref, ExportURLTTL)
		if err != nil {
			s.opts.Logger.Error("files: sign failed", "user", principal.UserID, "connector", connector, "id", id, "err", err)
			http.Error(w, "could not mint a download link", http.StatusInternalServerError)
			return
		}
		payload, _ := json.Marshal(struct {
			URL       string    `json:"url"`
			Bytes     int64     `json:"bytes"`
			ExpiresAt time.Time `json:"expires_at"`
		}{URL: url, Bytes: ref.Bytes, ExpiresAt: expiresAt})
		delivered := s.host.bridges.CompleteExport(id, principal.UserID, protocol.Result{ID: id, OK: true, Result: payload, DurationMs: float64(time.Since(start).Milliseconds())})
		if !delivered {
			// The export's tool call gave up (timeout, cancelled MCP request)
			// between UploadAuthorize and here. The bytes are stored and will
			// expire on the bucket's lifecycle rule; nobody is waiting for
			// the URL, so there is nothing else to do.
			s.opts.Logger.Info("files: upload landed after its export stopped waiting", "user", principal.UserID, "connector", connector, "id", id)
		}
		w.WriteHeader(http.StatusNoContent)
	}
}

// uploadLimiter is the per-user rate limit on export uploads (§10): a bug or
// a hostile pane retrying in a tight loop must not be able to hammer the
// file store or the IAM signBlob quota. One token every 2s, burst 3 — an
// agent exporting csv, xlsx and pdf back to back for the same workbook is
// the normal case this must not throttle.
type uploadLimiter struct {
	mu sync.Mutex
	m  map[string]*rate.Limiter
}

func newUploadLimiter() *uploadLimiter { return &uploadLimiter{m: map[string]*rate.Limiter{}} }

func (l *uploadLimiter) allow(user string) bool {
	l.mu.Lock()
	lim, ok := l.m[user]
	if !ok {
		lim = rate.NewLimiter(rate.Every(2*time.Second), 3)
		l.m[user] = lim
	}
	l.mu.Unlock()
	return lim.Allow()
}
