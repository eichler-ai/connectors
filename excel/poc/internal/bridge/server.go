package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"io/fs"
	"log"
	"net/http"
	"slices"
	"time"

	"golang.org/x/net/websocket"
)

// ExecRequest is the CLI → bridge body for POST /exec.
type ExecRequest struct {
	Script    string `json:"script"`
	TimeoutMs int64  `json:"timeoutMs,omitempty"`
}

// ExecResult is the bridge → CLI body. Either the add-in replied (Response populated) or the bridge
// itself failed (BridgeError set), e.g. no add-in connected or the deadline passed.
type ExecResult struct {
	Response
	BridgeError string `json:"bridgeError,omitempty"`
}

const (
	defaultTimeout = 30 * time.Second
	maxScriptBytes = 1 << 20
)

// Handler builds the bridge mux. addin is the directory of static add-in files served at /.
// extraOrigins are additional browser origins allowed to open /ws besides the bridge's own
// (e.g. Script Lab's runner when the client runs as a snippet there).
func Handler(h *Hub, addin fs.FS, extraOrigins ...string) http.Handler {
	mux := http.NewServeMux()
	mux.Handle("/ws", websocket.Server{
		// Office loads the task pane from this same origin, so a same-origin check is enough; a
		// nil Handshake would accept any Origin, which we do not want even for a POC.
		Handshake: func(cfg *websocket.Config, r *http.Request) error {
			origin := r.Header.Get("Origin")
			if origin != "https://"+r.Host && !slices.Contains(extraOrigins, origin) && !slices.Contains(extraOrigins, "*") {
				log.Printf("ws: rejected origin %q (host %s)", origin, r.Host)
				return errors.New("origin not allowed")
			}
			return nil
		},
		Handler: h.Serve,
	})
	mux.HandleFunc("GET /status", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, h.Status())
	})
	mux.HandleFunc("POST /exec", func(w http.ResponseWriter, r *http.Request) {
		body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, maxScriptBytes))
		if err != nil {
			http.Error(w, err.Error(), http.StatusRequestEntityTooLarge)
			return
		}
		var req ExecRequest
		if err := json.Unmarshal(body, &req); err != nil {
			http.Error(w, "bad JSON: "+err.Error(), http.StatusBadRequest)
			return
		}
		timeout := defaultTimeout
		if req.TimeoutMs > 0 {
			timeout = time.Duration(req.TimeoutMs) * time.Millisecond
		}
		ctx, cancel := context.WithTimeout(r.Context(), timeout)
		defer cancel()
		start := time.Now()
		resp, err := h.Exec(ctx, req.Script)
		if err != nil {
			log.Printf("exec: %v (after %s)", err, time.Since(start).Round(time.Millisecond))
			status := http.StatusBadGateway
			if errors.Is(err, ErrNotConnected) {
				status = http.StatusServiceUnavailable
			} else if errors.Is(err, context.DeadlineExceeded) {
				status = http.StatusGatewayTimeout
			}
			writeJSON(w, status, ExecResult{BridgeError: err.Error()})
			return
		}
		log.Printf("exec: %s ok=%v addin=%.0fms roundtrip=%s result=%dB", resp.ID, resp.OK, resp.DurationMs,
			time.Since(start).Round(time.Millisecond), len(resp.Result))
		writeJSON(w, http.StatusOK, ExecResult{Response: resp})
	})
	mux.Handle("/", http.FileServerFS(addin))
	return mux
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	enc := json.NewEncoder(w)
	enc.SetIndent("", "  ")
	_ = enc.Encode(v)
}
